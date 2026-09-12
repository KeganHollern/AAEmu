using System.Numerics;
using System.Text;
using AAEmu.Game.Models.CryEngine.Objects;

namespace AAEmu.UnitTests.Game.Models.CryEngine.Objects;

public sealed class CrySeparateWorldFilesTests
{
    [Test]
    public async Task DeferredBrushes_UseTheirOwnPathTablesAndBothSectorCoordinates()
    {
        using var file = Deferred((2, 3, Brush()), (3, 2, Brush()));
        using var assets = Paths(false, "game/objects/model.cgf");
        using var materials = Paths(false, "game/materials/material.mtl");
        var rows = CryDeferredBrushFile.Read(file, assets, materials);
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].SectorX).IsEqualTo(2);
        await Assert.That(rows[0].SectorY).IsEqualTo(3);
        await Assert.That(rows[0].AssetPath).IsEqualTo("game/objects/model.cgf");
        await Assert.That(rows[0].MaterialPath).IsEqualTo("game/materials/material.mtl");
        await Assert.That(rows[0].Brush.StartPos).IsEqualTo(new Vector3(129, 194, 3));
        await Assert.That(rows[0].Brush.EndPos).IsEqualTo(new Vector3(133, 198, 7));
        await Assert.That(rows[0].Brush.Matrix3X4.M14).IsEqualTo(138f);
        await Assert.That(rows[0].Brush.Matrix3X4.M24).IsEqualTo(212f);
        await Assert.That(rows[0].Brush.Matrix3X4.M34).IsEqualTo(30f);
        await Assert.That(rows[1].Brush.Matrix3X4.M14).IsEqualTo(202f);
        await Assert.That(rows[1].Brush.Matrix3X4.M24).IsEqualTo(148f);
        await Assert.That(rows[0].Brush.Data.Length).IsEqualTo(132);
        await Assert.That(file.CanRead && assets.CanRead && materials.CanRead).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task DeferredBrushes_RejectMalformedHeadersAndReferences(int failure)
    {
        using var original = Deferred((0, 0, Brush()));
        var data = original.ToArray();
        if (failure == 0) Write(data, 0, 2);
        if (failure == 1) Write(data, 4, 2051);
        if (failure == 2) Write(data, 1028, 127);
        if (failure == 3) Write(data, 2052 + 123, 1);
        if (failure == 4) Write(data, 2052 + 115, -1);
        using var file = new MemoryStream(data);
        using var assets = Paths(false, "model.cgf");
        using var materials = Paths(false, "material.mtl");
        await Assert.That(() => CryDeferredBrushFile.Read(file, assets, materials)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task BigObjects_ReadFlatRecordsAfterIndependentFlaggedPathTables()
    {
        var road = new byte[67];
        Write(road, 0, 13);
        using var file = Big(Brush(), road);
        var result = CryBigObjectsFile.Read(file);
        await Assert.That(result.AssetPaths.Single()).IsEqualTo("model.cgf");
        await Assert.That(result.MaterialPaths.Single()).IsEqualTo("material.mtl");
        await Assert.That(result.Objects.Count).IsEqualTo(2);
        await Assert.That(result.Objects[0]).IsTypeOf<ObjectDataType1Brush>();
        await Assert.That(result.Objects[1]).IsTypeOf<ObjectDataType13Road>();
        var brush = (ObjectDataType1Brush)result.Objects[0];
        await Assert.That(brush.Matrix3X4.M14).IsEqualTo(10f);
        await Assert.That(brush.StartPos).IsEqualTo(new Vector3(1, 2, 3));
        await Assert.That(file.CanRead).IsTrue();
    }

    [Test]
    public async Task BigObjects_UsesTheSuppliedReaderFactory()
    {
        using var file = Big(Brush());
        var visited = new List<ObjectDataType>();
        var result = CryBigObjectsFile.Read(file, type =>
        {
            visited.Add(type);
            return new ObjectDataType1Brush();
        });
        await Assert.That(visited.Single()).IsEqualTo(ObjectDataType.Brush);
        await Assert.That(result.Objects.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task BigObjects_RejectsUnknownTruncatedAndInvalidWaterRecords(int failure)
    {
        byte[] record;
        if (failure == 0) record = [1, 0];
        else if (failure == 1) record = BitConverter.GetBytes(500);
        else if (failure == 2) record = Brush()[..^1];
        else
        {
            record = new byte[123];
            Write(record, 0, 11);
            Write(record, 107, failure == 3 ? -1 : 1);
        }
        using var file = Big(record);
        await Assert.That(() => CryBigObjectsFile.Read(file)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task BigObjects_EmptyTablesAndRecordsAreValid()
    {
        using var file = new MemoryStream(new byte[8]);
        var result = CryBigObjectsFile.Read(file);
        await Assert.That(result.Objects.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeferredAndBigObjects_ExactClientCellIncludes330BrushesAndOneLargeWaterVolume()
    {
        var root = Environment.GetEnvironmentVariable("AAEMU_STATIC_WORLD_CLIENT_ROOT");
        Skip.Unless(!string.IsNullOrEmpty(root), "Set AAEMU_STATIC_WORLD_CLIENT_ROOT to extracted static client files.");
        var cell = Path.Combine(root!, "game/worlds/main_world/cells/026_008/client");
        using var file = File.OpenRead(Path.Combine(cell, "brush.dat"));
        using var assets = File.OpenRead(Path.Combine(cell, "statobjs.dat"));
        using var materials = File.OpenRead(Path.Combine(cell, "materials.dat"));
        var rows = CryDeferredBrushFile.Read(file, assets, materials);
        await Assert.That(rows.Count).IsEqualTo(330);
        await Assert.That(rows[0].SectorX).IsEqualTo(0);
        await Assert.That(rows[0].SectorY).IsEqualTo(10);
        await Assert.That(rows[0].Brush.PathId).IsEqualTo(3);
        await Assert.That(rows[0].Brush.Matrix3X4.M14).IsEqualTo(24.6083984375f);
        await Assert.That(rows[0].Brush.Matrix3X4.M24).IsEqualTo(702.015625f);
        await Assert.That(rows[0].Brush.Matrix3X4.M34).IsEqualTo(739.3059692382812f);
        using var bigFile = File.OpenRead(Path.Combine(cell, "big_object.dat"));
        var big = CryBigObjectsFile.Read(bigFile);
        await Assert.That(big.AssetPaths.Count).IsEqualTo(0);
        await Assert.That(big.MaterialPaths.Single()).IsEqualTo("game/materials/ocean/valley_river_watervolume_800a");
        await Assert.That(big.Objects.Count).IsEqualTo(1);
        await Assert.That(big.Objects[0]).IsTypeOf<ObjectDataType11Water>();
    }

    private static byte[] Brush()
    {
        var data = new byte[132];
        Write(data, 0, 1);
        for (var i = 0; i < 6; i++) BitConverter.TryWriteBytes(data.AsSpan(4 + i * 4), (float)(i < 3 ? i + 1 : i + 2));
        var matrix = new float[] { 1, 0, 0, 10, 0, 1, 0, 20, 0, 0, 1, 30 };
        for (var i = 0; i < matrix.Length; i++) BitConverter.TryWriteBytes(data.AsSpan(0x47 + i * 4), matrix[i]);
        return data;
    }

    private static MemoryStream Deferred(params (int X, int Y, byte[] Brush)[] rows)
    {
        var bytes = new byte[2052 + rows.Length * 128];
        Write(bytes, 0, 1);
        for (var i = 0; i < rows.Length; i++)
        {
            var sector = rows[i].X * 16 + rows[i].Y;
            Write(bytes, 4 + sector * 4, 2052 + i * 128);
            Write(bytes, 1028 + sector * 4, 128);
            rows[i].Brush.AsSpan(4).CopyTo(bytes.AsSpan(2052 + i * 128));
        }
        return new MemoryStream(bytes);
    }

    private static MemoryStream Big(params byte[][] records)
    {
        var stream = new MemoryStream();
        using var assets = Paths(true, "model.cgf");
        using var materials = Paths(true, "material.mtl");
        assets.CopyTo(stream);
        materials.CopyTo(stream);
        foreach (var record in records) stream.Write(record);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Paths(bool flags, params string[] paths)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(paths.Length);
        foreach (var path in paths)
        {
            if (flags) writer.Write(0);
            var bytes = new byte[256];
            Encoding.UTF8.GetBytes(path).CopyTo(bytes, 0);
            writer.Write(bytes);
        }
        stream.Position = 0;
        return stream;
    }

    private static void Write(byte[] bytes, int offset, int value) => BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
}
