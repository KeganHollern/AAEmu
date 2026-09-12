using System.Numerics;
using System.Text;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryGeometryResolverTests
{
    [Test]
    [Arguments("cgf://Objects//Trees///Palm.cgf", "cgf://objects/trees/palm.cgf")]
    [Arguments("Objects/\\Trees//Palm.cgf///", "objects/trees/palm.cgf/")]
    [Arguments("prefab://Prefabs//House.xml/House.Door", "prefab://prefabs/house.xml/house.door")]
    public async Task Normalize_CollapsesNativeSeparatorsAndPreservesUriAndTrailingSlash(string path, string expected)
    {
        await Assert.That(CryGeometryResolver.Normalize(path)).IsEqualTo(expected);
    }

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
    public async Task Load_MissingFile_ThrowsFileNotFound(string path)
    {
        var resolver = new CryGeometryResolver(_ => null);
        await Assert.That(() => resolver.Load(path)).Throws<FileNotFoundException>();
    }

    [Test]
    [Arguments("prefab://prefabs/missing.xml/house")]
    [Arguments("prefab://prefabs/present.xml/missing")]
    [Arguments("cga://objects/missing.chr")]
    public async Task Load_AbsentPrefabOrAnimation_HasNoGeometryOrModelBounds(string path)
    {
        var resolver = new CryGeometryResolver(name => name.EndsWith("present.xml", StringComparison.Ordinal)
            ? new MemoryStream(Encoding.UTF8.GetBytes("<PrefabsLibrary><Prefab Name=\"house\" /></PrefabsLibrary>")) : null);
        var asset = resolver.Load(path);
        await Assert.That(asset.HasModelBounds).IsFalse();
        await Assert.That(asset.Parts.Count).IsEqualTo(0);
        await Assert.That(asset.PoseRequirements.Count).IsEqualTo(0);
        if (path.StartsWith("cga://", StringComparison.Ordinal))
            await Assert.That(resolver.LoadCharacterPose(path, "idle", 10, true)).IsSameReferenceAs(asset);
    }

    [Test]
    public async Task Load_MissingBrush_UsesAuthoredFallbackAndKeepsMalformedErrors()
    {
        var resolver = new CryGeometryResolver(path => path.EndsWith("box_nodraw.cgf", StringComparison.Ordinal)
            ? new MemoryStream(Model(true)) : null);
        var asset = resolver.Load("cgf://objects/missing.cgf");
        await Assert.That(asset.Bounds.Min).IsEqualTo(-Vector3.One);
        await Assert.That(asset.HasModelBounds).IsTrue();
        var malformed = new CryGeometryResolver(_ => new MemoryStream(new byte[32]));
        await Assert.That(() => malformed.Load("cgf://objects/broken.cgf")).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task ReadCgf_AnimationOnlyAffectsCollisionThroughItsAncestors(bool animatedParent, bool expected)
    {
        var model = CryGeometryResolver.ReadCgf(Model(false, true, animatedParent));
        await Assert.That(model.Parts.Count).IsEqualTo(1);
        await Assert.That(model.HasAnimatedCollision).IsEqualTo(expected);
    }

    [Test]
    public async Task LoadPrefab_ActiveVisualAndPassiveDoor_DoesNotMarkTheDoorCollisionActive()
    {
        var xml = Encoding.UTF8.GetBytes("""
            <PrefabsLibrary><Prefab Name="house"><Objects>
            <Object Type="Entity"><Properties object_Model="objects/visual.cga">
            <Animation bPlaying="1" Animation="Default" /></Properties></Object>
            <Object Type="Entity"><Properties object_Model="objects/door.cga">
            <Animation bPlaying="0" Animation="Default" /></Properties></Object>
            </Objects></Prefab></PrefabsLibrary>
            """);
        var resolver = new CryGeometryResolver(path => new MemoryStream(path.EndsWith(".xml", StringComparison.Ordinal)
            ? xml : path.EndsWith("door.cga", StringComparison.Ordinal) ? Model(false, true, true) : Model(false)));
        var asset = resolver.Load("prefab://prefabs/housing.xml/house");
        await Assert.That(asset.HasAnimatedCollision).IsTrue();
        await Assert.That(asset.PoseRequirements.Count).IsEqualTo(1);
        await Assert.That(asset.PoseRequirements[0].AffectsCollision).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Load_Character_DefaultOnlyStartsWhenItsCalDefinesIt(bool hasDefault)
    {
        var resolver = new CryGeometryResolver(path => new MemoryStream(path.EndsWith(".chr", StringComparison.Ordinal)
            ? Model(false, true, true)
            : Encoding.UTF8.GetBytes(path.EndsWith("model.cal", StringComparison.Ordinal)
                ? "#filepath = animations/cat\n$Include = objects/shared.cal\n"
                : hasDefault ? "Default = idle.caf // clip\n" : "idle = idle.caf\n")));
        var asset = resolver.Load("cga://objects/model.chr");
        await Assert.That(asset.PoseRequirements.Count).IsEqualTo(hasDefault ? 1 : 0);
    }

    [Test]
    public async Task LoadPrefab_VisualEffectsWithoutModel_ContributeNoSolidGeometryOrModelBounds()
    {
        var xml = Encoding.UTF8.GetBytes("""
            <PrefabsLibrary><Prefab Name="effect"><Objects>
              <Object Type="Comment" Name="note" Pos="100,100,100" />
              <Object Type="Entity" EntityClass="ParticleEffect"><Properties ParticleEffect="smoke" /></Object>
              <Object Type="Decal" Pos="10,10,10" />
            </Objects></Prefab></PrefabsLibrary>
            """);
        var resolver = new CryGeometryResolver(_ => new MemoryStream(xml));
        var asset = resolver.Load("prefab://prefabs/fx.xml/effect");
        await Assert.That(asset.HasModelBounds).IsFalse();
        await Assert.That(asset.Parts.Count).IsEqualTo(0);
        await Assert.That(asset.Helpers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LoadPrefab_MissingAnimationChild_DoesNotExtendTheOtherModelBounds()
    {
        var xml = Encoding.UTF8.GetBytes("""
            <PrefabsLibrary><Prefab Name="house"><Objects>
              <Object Type="Brush" Prefab="objects/house.cgf" Pos="10,10,10" />
              <Object Type="Entity" Pos="100,100,100"><Properties object_Model="cga://objects/missing.cga" /></Object>
            </Objects></Prefab></PrefabsLibrary>
            """);
        var resolver = new CryGeometryResolver(path => path.EndsWith("missing.cga", StringComparison.Ordinal) ? null :
            new MemoryStream(path.EndsWith(".xml", StringComparison.Ordinal) ? xml : Model(true)));
        var asset = resolver.Load("prefab://prefabs/housing.xml/house");
        await Assert.That(asset.HasModelBounds).IsTrue();
        await Assert.That(asset.Bounds.Max).IsEqualTo(new Vector3(11, 11, 11));
    }

    private static byte[] Model(bool merge, bool physics = false, bool animatedParent = false)
    {
        var chunks = new List<(uint Kind, int Version, int Id, byte[] Data)>();
        chunks.Add((0xcccc0015, 1, 1, BitConverter.GetBytes(merge ? 1 : 0)));
        chunks.Add((0xcccc0000, 0x800, 2, Mesh(Vector3.One, 3)));
        var proxyMesh = Mesh(new Vector3(2), 0);
        if (physics)
        {
            BitConverter.GetBytes(7).CopyTo(proxyMesh, 92);
            // Serialized native sphere, including the physical header and box tree.
            var proxy = new byte[24 + 156];
            BitConverter.GetBytes(156).CopyTo(proxy, 0);
            BitConverter.GetBytes(1).CopyTo(proxy, 24);
            BitConverter.GetBytes(4).CopyTo(proxy, 24 + 68);
            BitConverter.GetBytes(1f).CopyTo(proxy, 24 + 84);
            chunks.Add((0xcccc0018, 0x800, 7, proxy));
        }
        chunks.Add((0xcccc0000, 0x800, 3, proxyMesh));
        chunks.Add((0xcccc000b, 0x823, 4, Node("house", 2, -1, 1000, animatedParent ? 6 : -1)));
        chunks.Add((0xcccc000b, 0x823, 5, Node("proxy", 3, 4, 1000)));
        var controller = new byte[52];
        BitConverter.GetBytes(9).CopyTo(controller, 0);
        BitConverter.GetBytes(1).CopyTo(controller, 4);
        chunks.Add((0xcccc000d, 0x826, 6, controller));
        // This animated visual node has no collision and is not a parent of the proxy.
        chunks.Add((0xcccc000b, 0x823, 8, Node("visual", -1, 4, 0, 6)));
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

    private static byte[] Node(string name, int mesh, int parent, float x, int controller = -1)
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
        stream.Position = 188;
        writer.Write(controller);
        writer.Write(-1);
        writer.Write(-1);
        return stream.ToArray();
    }
}
