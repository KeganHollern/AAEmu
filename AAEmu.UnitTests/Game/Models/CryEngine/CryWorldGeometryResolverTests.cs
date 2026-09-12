using System.Numerics;

using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.CryEngine;

public sealed class CryWorldGeometryResolverTests
{
    [Test]
    public async Task MissingBrush_UsesAndCachesNativeFallback()
    {
        var fallback = new CryGeometryAsset(new CryBounds(Vector3.Zero, Vector3.One), []);
        var calls = new List<string>();
        var resolver = new CryWorldGeometryResolver(path =>
        {
            calls.Add(path);
            return path == "objects/box_nodraw.cgf" ? fallback :
                throw new FileNotFoundException("fixture", "game/objects/missing.cgf");
        });
        var row = Instance("cgf://objects/missing.cgf");
        await Assert.That(resolver.Load(row)).IsSameReferenceAs(fallback);
        await Assert.That(resolver.Load(row)).IsSameReferenceAs(fallback);
        await Assert.That(calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SuppliedVoxel_UsesExactAssetWithoutModelLoad()
    {
        var asset = new CryGeometryAsset(new CryBounds(Vector3.Zero, Vector3.One), []);
        var resolver = new CryWorldGeometryResolver(_ => throw new InvalidOperationException("Unexpected model load."));
        await Assert.That(resolver.Load(Instance("") with { Kind = ObjectDataType.Voxel, Asset = asset }))
            .IsSameReferenceAs(asset);
    }

    [Test]
    public async Task BrokenOrUnsupportedBrush_DoesNotBecomeAnEmptyFallback()
    {
        var broken = new CryWorldGeometryResolver(_ => throw new InvalidDataException("Truncated CGF."));
        await Assert.That(() => broken.Load(Instance("objects/broken.cgf"))).Throws<InvalidDataException>();
        var unsupported = new CryWorldGeometryResolver(_ => throw new NotSupportedException("Unsupported shape."));
        await Assert.That(() => unsupported.Load(Instance("objects/broken.cgf"))).Throws<NotSupportedException>();
    }

    [Test]
    public async Task MissingNestedAssetOrVegetation_DoesNotUseBrushFallback()
    {
        var resolver = new CryWorldGeometryResolver(_ =>
            throw new FileNotFoundException("fixture", "game/objects/child.cgf"));
        await Assert.That(() => resolver.Load(Instance("prefab://objects/library.xml/house")))
            .Throws<FileNotFoundException>();
        await Assert.That(() => resolver.Load(Instance("objects/parent.cgf")))
            .Throws<FileNotFoundException>();
        await Assert.That(() => resolver.Load(Instance("objects/child.cgf") with { Kind = ObjectDataType.Vegetation }))
            .Throws<FileNotFoundException>();
    }

    [Test]
    public async Task MissingFallback_RemainsAnError()
    {
        var resolver = new CryWorldGeometryResolver(path =>
            throw new FileNotFoundException("fixture", "game/" + path));
        await Assert.That(() => resolver.Load(Instance("objects/missing.cgf"))).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task ExactClient_LoadsEveryRetainedStaticModelAndNativeFallback()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 static model check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            var missing = new HashSet<string>();
            Stream Open(string name)
            {
                if (source.FileExists(name)) return source.GetFileStream(name);
                if (name.EndsWith(".cgf", StringComparison.Ordinal)) missing.Add(name);
                return null;
            }
            var models = new CryGeometryResolver(Open);
            var resolver = new CryWorldGeometryResolver(name =>
            {
                try { return models.Load(name); }
                catch (FileNotFoundException) { missing.Add(name); throw; }
            });
            var world = new WorldTemplate { Name = "main_world", Cells = new WorldCell[35, 38] };
            var index = CryWorldObjectIndex.Load(world, Open, name => models.Load(name).Parts.Any(part =>
                CryGeometryLayerRules.GetVegetationUsage(part.PhysicsType) != CryGeometryQueryUsage.None));
            await Assert.That(index.Count).IsEqualTo(279447);
            var all = index.Query(new(-1000, -1000, -10000), new(40000, 40000, 10000));
            foreach (var row in all.DistinctBy(row => (row.Kind, row.ModelUri)))
                await Assert.That(resolver.Load(row)).IsNotNull();
            await Assert.That(missing.Count).IsEqualTo(8);
            await Assert.That(models.Load("objects/default.cgf").Parts.Count).IsEqualTo(0);
            await Assert.That(models.Load("objects/box_nodraw.cgf").Parts.Count).IsEqualTo(0);
            foreach (var row in all.Where(row => missing.Contains(row.ModelUri)))
                await Assert.That(resolver.Load(row).Parts.Count).IsEqualTo(0);
            Console.WriteLine("r208022 static geometry: 279447 instances, 116832 physical vegetation instances, 8 native brush fallbacks.");
        }
        finally { source.Close(); }
    }

    private static CryWorldObjectInstance Instance(string path) =>
        new(path, Matrix4x4.Identity, Vector3.Zero, Vector3.One, "fixture");
}
