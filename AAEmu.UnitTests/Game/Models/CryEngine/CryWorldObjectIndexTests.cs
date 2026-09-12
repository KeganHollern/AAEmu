using System.Numerics;
using System.Text;

using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.World;

using CgfConverter.Structs;

namespace AAEmu.UnitTests.Game.Models.CryEngine;

public sealed class CryWorldObjectIndexTests
{
    [Test]
    public async Task BrushTransform_PreservesRotationScaleAndCellTranslation()
    {
        var objects = new ObjectsFile("fixture")
        {
            AssetPathsList = [new AssetPath { Name = "objects\\wall.cgf" }],
            MaterialPathsList = [new AssetPath { Name = "materials\\stone" }],
            PrefabsList = [new ObjectDataType1Brush
            {
                PathId = 0, StartPos = new(10, 20, 30), EndPos = new(20, 30, 40),
                Matrix3X4 = new Matrix3x4
                {
                    M11 = 0, M12 = -3, M13 = 0, M14 = 10,
                    M21 = 2, M22 = 0, M23 = 0, M24 = 20,
                    M31 = 0, M32 = 0, M33 = 4, M34 = 30
                }
            }]
        };
        var instance = CryWorldObjectIndex.ReadBrushInstances(objects, 2, 3).Single();
        await Assert.That(instance.ModelUri).IsEqualTo("objects/wall.cgf");
        await Assert.That(instance.MaterialPath).IsEqualTo("materials/stone");
        await Assert.That(Vector3.Transform(new Vector3(1, 2, 3), instance.Transform)).IsEqualTo(new Vector3(2052, 3094, 42));
        await Assert.That(instance.Min).IsEqualTo(new Vector3(2058, 3092, 30));
        await Assert.That(instance.Max).IsEqualTo(new Vector3(2068, 3102, 40));
    }

    [Test]
    public async Task Query_CrossCellExtentAndMultipleBuckets_ReturnsEachInstanceOnce()
    {
        var far = Instance("far", new(100, 100, 50), new(140, 140, 70));
        var spanning = Instance("spanning", new(1000, 1000, 0), new(2100, 1200, 10));
        var index = new CryWorldObjectIndex([far, spanning]);
        await Assert.That(index.Query(new(2000, 1000, 0), new(2200, 1300, 10)).Single()).IsEqualTo(spanning);
        await Assert.That(index.Query(new(1000, 1000, 11), new(2100, 1200, 12)).Count).IsEqualTo(0);
        await Assert.That(index.Query(new(90, 90, 0), new(2100, 1200, 70)).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Query_NegativeBucketAndTouchingBoundary_PreservesCandidates()
    {
        var index = new CryWorldObjectIndex([Instance("edge", new(-64, -5, -1), new(0, 5, 1))]);
        await Assert.That(index.Query(new(0, 0, 0), new(1, 1, 1)).Count).IsEqualTo(1);
        await Assert.That(index.Query(new(-65, -1, 0), new(-64, 1, 1)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Load_UsesSeparateWorldFilesAndKeepsAuthoredTransforms()
    {
        var world = new WorldTemplate { Name = "test", Cells = new WorldCell[2, 1] };
        var seen = new List<string>();
        var index = CryWorldObjectIndex.Load(world, path =>
        {
            seen.Add(path);
            return path.Contains("001_000", StringComparison.Ordinal) ? BrushFile() : null;
        });
        await Assert.That(index.Count).IsEqualTo(1);
        var brush = index.Query(new(1024, 0, 0), new(1034, 10, 10)).Single();
        await Assert.That(Vector3.Transform(Vector3.Zero, brush.Transform)).IsEqualTo(new Vector3(1029, 5, 5));
        await Assert.That(seen[0]).IsEqualTo("game/worlds/test/cells/000_000/client/object.dat");
        await Assert.That(seen[1]).IsEqualTo("game/worlds/test/cells/001_000/client/object.dat");
        await Assert.That(CryWorldObjectIndex.Load(new WorldTemplate { Name = "other" }, _ => null).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Load_MalformedBrushAndUnknownAsset_FailInsteadOfOmittingCollision()
    {
        var objects = new ObjectsFile("bad") { PrefabsList = [new ObjectDataType1Brush { PathId = 7 }] };
        await Assert.That(() => CryWorldObjectIndex.ReadBrushInstances(objects, 0, 0)).Throws<InvalidDataException>();
        using var valid = BrushFile();
        var truncated = valid.ToArray()[..^1];
        await Assert.That(() => CryWorldObjectIndex.Load(new WorldTemplate(), _ => new MemoryStream(truncated)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task ExactClient_LoadsAllMainWorldBrushes()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 world object check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            var world = new WorldTemplate { Name = "main_world", Cells = new WorldCell[35, 38] };
            var files = 0;
            var index = CryWorldObjectIndex.Load(world, name =>
            {
                if (!source.FileExists(name))
                    return null;
                files++;
                return source.GetFileStream(name);
            });
            await Assert.That(files).IsEqualTo(1205);
            await Assert.That(index.Count).IsEqualTo(162615);
            var all = index.Query(new(-1000, -1000, -10000), new(40000, 40000, 10000));
            await Assert.That(all.Count(row => row.Kind == ObjectDataType.Brush)).IsEqualTo(162386);
            await Assert.That(all.Count(row => row.Kind == ObjectDataType.Voxel)).IsEqualTo(229);
            foreach (var voxel in all.Where(row => row.Kind == ObjectDataType.Voxel))
            {
                await Assert.That(voxel.Asset.Parts.Count).IsGreaterThan(0);
                await Assert.That(voxel.TerrainSurfaceNames.Count).IsEqualTo(32);
                await Assert.That(voxel.Asset.Parts.All(part => part.Shape is CryTriangleMesh)).IsTrue();
            }
            Console.WriteLine($"r208022 main_world: {files} object.dat files, 162386 brushes, 229 authored voxel meshes.");
        }
        finally
        {
            source.Close();
        }
    }

    private static CryWorldObjectInstance Instance(string name, Vector3 min, Vector3 max) =>
        new(name, Matrix4x4.Identity, min, max, name);

    private static MemoryStream BrushFile()
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(1u);
            writer.Write(0u);
            var asset = new byte[256];
            Encoding.UTF8.GetBytes("objects/wall.cgf").CopyTo(asset, 0);
            writer.Write(asset);
            writer.Write(1u);
            writer.Write(new byte[260]);
            writer.Write(2u);
            foreach (var value in new float[] { 0, 0, 0, 8192, 8192, 8192 })
                writer.Write(value);
            writer.Write(132);
            writer.Write((byte)0);
            var brush = new byte[132];
            BitConverter.GetBytes(1).CopyTo(brush, 0);
            for (var axis = 0; axis < 3; axis++)
            {
                BitConverter.GetBytes(10f).CopyTo(brush, 0x10 + axis * 4);
                BitConverter.GetBytes(1f).CopyTo(brush, 0x47 + axis * 20);
                BitConverter.GetBytes(5f).CopyTo(brush, 0x53 + axis * 16);
            }
            writer.Write(brush);
        }
        stream.Position = 0;
        return stream;
    }
}
