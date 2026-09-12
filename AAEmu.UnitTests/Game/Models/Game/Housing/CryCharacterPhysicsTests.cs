using System.Numerics;
using System.Text;

using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryCharacterPhysicsTests
{
    [Test]
    [Arguments(0x800)]
    [Arguments(0x801)]
    public async Task Read_BoneCapsule_UsesAuthoredWorldBindTransform(int boneVersion)
    {
        var bones = CryCharacterPhysicsReader.Read(Bones(boneVersion), boneVersion, Capsule(), 0x801);
        await Assert.That(bones.Count).IsEqualTo(2);
        await Assert.That(bones[1].ParentIndex).IsEqualTo(0);
        await Assert.That(bones[1].BindTransform.Translation).IsEqualTo(new Vector3(10, 20, 30));
        await Assert.That(Vector3.TransformNormal(Vector3.UnitX, bones[1].BindTransform)).IsEqualTo(Vector3.UnitY);
        var capsule = (CryCylinder)bones[1].Shape;
        await Assert.That(capsule.IsCapsule).IsTrue();
        await Assert.That(capsule.Radius).IsEqualTo(0.25f);
        await Assert.That(capsule.HalfHeight).IsEqualTo(0.5f);
        await Assert.That(capsule.SurfaceIndex).IsEqualTo(7);
    }

    [Test]
    public async Task Read_MissingReferencedProxy_LeavesBoneWithoutGeometry()
    {
        var bones = CryCharacterPhysicsReader.Read(Bones(0x801), 0x801, new byte[4], 0x801);
        await Assert.That(bones.Count).IsEqualTo(2);
        await Assert.That(bones[1].Name).IsEqualTo("neck");
        await Assert.That(bones[1].Shape).IsNull();
    }

    [Test]
    public async Task Read_RepeatedProxyId_TransfersGeometryToFirstBoneOnly()
    {
        var bytes = Bones(0x801);
        BitConverter.GetBytes(23).CopyTo(bytes, 32);
        var bones = CryCharacterPhysicsReader.Read(bytes, 0x801, Capsule(), 0x801);
        await Assert.That(bones[0].Shape).IsNotNull();
        await Assert.That(bones[1].Shape).IsNull();
    }

    [Test]
    public async Task Read_TruncatedPrimitive_Throws()
    {
        await Assert.That(() => CryCharacterPhysicsReader.Read(Bones(0x801), 0x801, Capsule()[..^1], 0x801))
            .Throws<EndOfStreamException>();
    }

    [Test]
    public async Task ExactClient_MissingBoneReferencesPreserveOtherAuthoredProxies()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 bone reference check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            var resolver = new CryGeometryResolver(name => source.FileExists(name) ? source.GetFileStream(name) : null);
            await Assert.That(resolver.Load("objects/characters/monster/mermaid/mermaid.chr").Parts.Count).IsEqualTo(12);
            await Assert.That(resolver.Load("prefab://prefabs/interaction_b.xml/quest.ferre_skeleton").Parts.Count).IsEqualTo(6);
            await Assert.That(resolver.Load("prefab://prefabs/quest_h2co3.xml/14east.skeleton").Parts.Count).IsEqualTo(7);
        }
        finally { source.Close(); }
    }

    private static byte[] Bones(int version)
    {
        var stride = version == 0x801 ? 324 : 584;
        using var stream = new MemoryStream(new byte[32 + 2 * stride], true);
        using var writer = new BinaryWriter(stream);
        for (var i = 0; i < 2; i++)
        {
            var start = 32 + i * stride;
            stream.Position = start + (version == 0x801 ? 0 : 4);
            writer.Write(i == 0 ? -1 : 23);
            writer.Write(0x600);
            stream.Position = start + (version == 0x801 ? 208 : 0);
            writer.Write((uint)(123 + i));
            stream.Position = start + (version == 0x801 ? 216 : 312);
            writer.Write(Encoding.ASCII.GetBytes(i == 0 ? "root" : "neck"));
            stream.Position = start + (version == 0x801 ? 264 : 572);
            writer.Write(i == 0 ? 0 : -1);
            stream.Position = start + (version == 0x801 ? 276 : 264);
            foreach (var value in new[] { 0f, -1, 0, 10, 1, 0, 0, 20, 0, 0, 1, 30 })
                writer.Write(value);
        }
        return stream.ToArray();
    }

    private static byte[] Capsule()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(23);
        // The compiled primitive retains source tessellation counts without those arrays.
        writer.Write(146);
        writer.Write(864);
        writer.Write(288);
        writer.Write((byte)6);
        foreach (var value in new[] { 0f, 0, 0, 0, 0, 1, 0.25f, 0.5f })
            writer.Write(value);
        writer.Write((byte)7);
        return stream.ToArray();
    }
}
