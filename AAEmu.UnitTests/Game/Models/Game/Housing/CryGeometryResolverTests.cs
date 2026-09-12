using System.Numerics;
using System.Text;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryGeometryResolverTests
{
    [Test]
    public async Task ReadCgf_CompoundModel_UsesParentTransformAndEmptyProxyBounds()
    {
        var model = CryGeometryResolver.ReadCgf(Model(false));
        await Assert.That(model.Bounds.Min).IsEqualTo(new Vector3(9, -2, -2));
        await Assert.That(model.Bounds.Max).IsEqualTo(new Vector3(22, 2, 2));
    }

    [Test]
    public async Task ReadCgf_MergedModel_UsesFirstMeshBounds()
    {
        var model = CryGeometryResolver.ReadCgf(Model(true));
        await Assert.That(model.Bounds.Min).IsEqualTo(-Vector3.One);
        await Assert.That(model.Bounds.Max).IsEqualTo(Vector3.One);
    }

    [Test]
    public async Task LoadPrefab_TransformAndComment_PreserveNativeCoordinates()
    {
        var xml = Encoding.UTF8.GetBytes("""
            <PrefabsLibrary><Prefab Name="house"><Objects>
            <Object Type="Comment" Name="connector" Comment="(height:12)(+x)" Pos="1,2,3" />
            <Object Type="Brush" Prefab="objects/house.cgf" Pos="100,200,300" Scale="2,2,2" Rotate="1,0,0,0" />
            </Objects></Prefab></PrefabsLibrary>
            """);
        var resolver = new CryGeometryResolver(path => new MemoryStream(path.EndsWith(".xml", StringComparison.Ordinal) ? xml : Model(true)));
        var model = resolver.Load("prefab://Prefabs/housing.xml/house");
        await Assert.That(model.Bounds.Min).IsEqualTo(new Vector3(98, 198, 298));
        await Assert.That(model.Bounds.Max).IsEqualTo(new Vector3(102, 202, 302));
        await Assert.That(model.Helpers.Count).IsEqualTo(1);
        await Assert.That(model.Helpers[0].Text).IsEqualTo("(height:12)(+x)");
        await Assert.That(model.Helpers[0].Transform.Translation).IsEqualTo(new Vector3(1, 2, 3));
    }

    [Test]
    public async Task ReadCgf_BadChunkRange_Throws()
    {
        var bytes = Model(false);
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 16);
        await Assert.That(() => CryGeometryResolver.ReadCgf(bytes)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Load_UnknownDoodadScheme_UsesNativeInvisibleBox()
    {
        string loaded = null;
        var resolver = new CryGeometryResolver(path =>
        {
            loaded = path;
            return new MemoryStream(Model(true));
        });
        var model = resolver.Load("a://invalid");
        await Assert.That(loaded).IsEqualTo("game/objects/box_nodraw.cgf");
        await Assert.That(model.Bounds.Max).IsEqualTo(Vector3.One);
    }

    [Test]
    [Arguments("cgf://objects/missing.cgf")]
    [Arguments("prefab://prefabs/missing.xml/house")]
    public async Task Load_MissingFile_ThrowsFileNotFound(string path)
    {
        var resolver = new CryGeometryResolver(_ => null);
        await Assert.That(() => resolver.Load(path)).Throws<FileNotFoundException>();
    }

    private static byte[] Model(bool merge)
    {
        var chunks = new List<(uint Kind, int Version, int Id, byte[] Data)>();
        chunks.Add((0xcccc0015, 1, 1, BitConverter.GetBytes(merge ? 1 : 0)));
        chunks.Add((0xcccc0000, 0x800, 2, Mesh(Vector3.One, 3)));
        chunks.Add((0xcccc0000, 0x800, 3, Mesh(new Vector3(2), 0)));
        chunks.Add((0xcccc000b, 0x823, 4, Node("house", 2, -1, 1000)));
        chunks.Add((0xcccc000b, 0x823, 5, Node("proxy", 3, 4, 1000)));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("CryTek\0\0"));
        writer.Write(0xffff0000);
        writer.Write(0x744);
        writer.Write(0);
        var offsets = new List<int>();
        foreach (var (kind, version, id, data) in chunks)
        {
            offsets.Add((int)stream.Position);
            writer.Write(kind);
            writer.Write(version);
            writer.Write((int)stream.Position - 8);
            writer.Write(id);
            writer.Write(data);
        }
        var table = (int)stream.Position;
        writer.Write(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            writer.Write(chunks[i].Kind);
            writer.Write(chunks[i].Version);
            writer.Write(offsets[i]);
            writer.Write(chunks[i].Id);
        }
        stream.Position = 16;
        writer.Write(table);
        return stream.ToArray();
    }

    private static byte[] Mesh(Vector3 half, int vertices)
    {
        using var stream = new MemoryStream(new byte[260], true);
        using var writer = new BinaryWriter(stream);
        stream.Position = 8;
        writer.Write(vertices);
        stream.Position = 108;
        foreach (var value in new[] { -half.X, -half.Y, -half.Z, half.X, half.Y, half.Z })
            writer.Write(value);
        return stream.ToArray();
    }

    private static byte[] Node(string name, int mesh, int parent, float x)
    {
        using var stream = new MemoryStream(new byte[204], true);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes(name));
        stream.Position = 64;
        writer.Write(mesh);
        writer.Write(parent);
        stream.Position = 84;
        foreach (var value in new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, 0, 0, 0 })
            writer.Write(value);
        return stream.ToArray();
    }
}
