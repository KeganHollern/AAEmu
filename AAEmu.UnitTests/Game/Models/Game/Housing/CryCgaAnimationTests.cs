using System.Numerics;
using System.Text;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryCgaAnimationTests
{
    [Test]
    public async Task Sample_Translation_UpdatesProxyAndRenderBounds()
    {
        var animation = Read([(0, Vector3.Zero, 0), (4800, new Vector3(200, 0, 0), 0)], false);
        var pose = animation.Sample(Asset(), 0.5, false);
        await Assert.That(Vector3.Distance(pose.Parts[0].Transform.Translation, new Vector3(1, 0, 0))).IsLessThan(0.00001f);
        await Assert.That(Vector3.Distance(pose.Bounds.Min, new Vector3(0, -1, -1))).IsLessThan(0.00001f);
        await Assert.That(pose.PoseRequirements.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Sample_Rotation_AccumulatesDeltasAndConjugatesNativeController()
    {
        var animation = Read([(0, Vector3.UnitZ, MathF.PI / 2), (4800, Vector3.UnitZ, MathF.PI / 2)], true);
        var pose = animation.Sample(Asset(), 0, false);
        var direction = Vector3.TransformNormal(Vector3.UnitX, pose.Parts[0].Transform);
        await Assert.That(Vector3.Distance(direction, -Vector3.UnitY)).IsLessThan(0.00001f);
        pose = animation.Sample(Asset(), 10, false);
        direction = Vector3.TransformNormal(Vector3.UnitX, pose.Parts[0].Transform);
        await Assert.That(Vector3.Distance(direction, -Vector3.UnitX)).IsLessThan(0.00001f);
    }

    [Test]
    public async Task Sample_RotationMidpoint_UsesQuaternionTangents()
    {
        var animation = Read([(0, Vector3.UnitZ, 0), (4800, Vector3.UnitZ, MathF.PI / 2)], true);
        var pose = animation.Sample(Asset(), 0.5, false);
        var direction = Vector3.TransformNormal(Vector3.UnitX, pose.Parts[0].Transform);
        await Assert.That(Vector3.Distance(direction, Vector3.Normalize(new Vector3(1, -1, 0)))).IsLessThan(0.00001f);
    }

    [Test]
    public async Task Sample_TcbMiddleAndEndpointTangents_UseAuthoredCurvature()
    {
        var animation = Read([(0, Vector3.Zero, 0), (4800, new Vector3(100, 0, 0), 0),
            (9600, new Vector3(300, 0, 0), 0)], false);
        var pose = animation.Sample(Asset(), 0.5, false);
        // Native endpoint tangent is -75 cm, and the middle incoming tangent is 150 cm.
        await Assert.That(MathF.Abs(pose.Parts[0].Transform.M41 - 0.21875f)).IsLessThan(0.00001f);
    }

    [Test]
    public async Task Sample_LoopAndParent_UseClipTimeAndHierarchy()
    {
        var animation = Read([(0, Vector3.Zero, 0), (4800, new Vector3(200, 0, 0), 0)], false, true);
        var pose = animation.Sample(Asset() with
        {
            Parts = [Asset().Parts[0] with { CgaNodeId = 2, Transform = Matrix4x4.CreateTranslation(0, 3, 0) }]
        }, 1.5, true);
        await Assert.That(Vector3.Distance(pose.Parts[0].Transform.Translation, new Vector3(1, 3, 0))).IsLessThan(0.00001f);
    }

    [Test]
    public async Task Sample_NarrowMesh_UsesNativeBoundsPadding()
    {
        var animation = Read([(0, Vector3.Zero, 0), (4800, Vector3.Zero, 0)], false, narrow: true);
        var pose = animation.Sample(Asset(), 0, false);
        await Assert.That(Vector3.Distance(pose.Bounds.Min, new Vector3(-0.3f, -1, -1))).IsLessThan(0.00001f);
        await Assert.That(Vector3.Distance(pose.Bounds.Max, new Vector3(0.3f, 1, 1))).IsLessThan(0.00001f);
    }

    [Test]
    public async Task GetBindBounds_PreviewUsesBindPoseAndNativePadding()
    {
        var animation = Read([(0, new Vector3(200, 0, 0), 0), (4800, new Vector3(400, 0, 0), 0)], false, narrow: true);
        var bounds = animation.GetBindBounds();
        await Assert.That(Vector3.Distance(bounds.Min, new Vector3(-0.3f, -1, -1))).IsLessThan(0.00001f);
        await Assert.That(Vector3.Distance(bounds.Max, new Vector3(0.3f, 1, 1))).IsLessThan(0.00001f);
        await Assert.That(animation.Sample(Asset(), 0, false).Bounds.Center.X).IsEqualTo(2f);
    }

    [Test]
    public async Task WithClip_NodeName_MapsDifferentControllerIdsToTheBindModel()
    {
        var animation = Read([(0, Vector3.Zero, 0), (4800, new Vector3(200, 0, 0), 0)], false, remap: true);
        var pose = animation.Sample(Asset(), 0.5, false);
        await Assert.That(MathF.Abs(pose.Parts[0].Transform.M41 - 1)).IsLessThan(0.00001f);
    }

    [Test]
    public async Task WithClip_DuplicateNames_UpdateOnlyTheFirstBaseJoint()
    {
        var animation = Read([(0, Vector3.Zero, 0), (4800, new Vector3(200, 0, 0), 0)], false, child: true, remap: true);
        var pose = animation.Sample(Asset() with
        {
            Parts = [Asset().Parts[0] with { CgaNodeId = 2, Transform = Matrix4x4.CreateTranslation(0, 3, 0) }]
        }, 0.5, false);
        await Assert.That(Vector3.Distance(pose.Parts[0].Transform.Translation, new Vector3(1, 3, 0))).IsLessThan(0.00001f);
    }

    private static CryGeometryAsset Asset() => new(new CryBounds(-Vector3.One, Vector3.One),
        [new CryGeometryPart(new CrySphere(Vector3.Zero, 1), Matrix4x4.Identity, 0x1000, "", "root") { CgaNodeId = 1 }])
    {
        PoseRequirements = [new CryGeometryPoseRequirement("model.cga", "", Matrix4x4.Identity, "Default", true, true)]
    };

    private static CryCgaAnimation Read((int Time, Vector3 Value, float Angle)[] keys, bool rotation, bool child = false,
        bool narrow = false, bool remap = false)
    {
        var chunks = new List<(uint Kind, int Version, int Id, byte[] Body)>();
        using var timing = new MemoryStream();
        using (var writer = new BinaryWriter(timing, Encoding.UTF8, true))
        {
            writer.Write(1f / 4800);
            writer.Write(160);
            writer.Write(new byte[32]);
            writer.Write(0);
            writer.Write(keys[^1].Time / 160);
        }
        chunks.Add((0xcccc000e, 0x918, 3, timing.ToArray()));
        using var track = new MemoryStream();
        using (var writer = new BinaryWriter(track, Encoding.UTF8, true))
        {
            writer.Write(rotation ? 10 : 9);
            writer.Write(keys.Length);
            writer.Write(0);
            writer.Write(4);
            foreach (var key in keys)
            {
                writer.Write(key.Time);
                writer.Write(key.Value.X);
                writer.Write(key.Value.Y);
                writer.Write(key.Value.Z);
                if (rotation)
                    writer.Write(key.Angle);
                writer.Write(new byte[20]);
            }
        }
        chunks.Add((0xcccc000d, 0x826, remap ? 17 : 4, track.ToArray()));
        if (remap)
        {
            var node = new byte[200];
            Encoding.UTF8.GetBytes("root").CopyTo(node, 0);
            BitConverter.GetBytes(-1).CopyTo(node, 188);
            BitConverter.GetBytes(-1).CopyTo(node, 192);
            BitConverter.GetBytes(-1).CopyTo(node, 196);
            chunks.Add((0xcccc000b, 0x823, 12, node.ToArray()));
            BitConverter.GetBytes(17).CopyTo(node, 188);
            chunks.Add((0xcccc000b, 0x823, 13, node));
        }
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream);
        output.Write(Encoding.ASCII.GetBytes("CryTek\0\0"));
        output.Write(0xffff0000);
        output.Write(0x745);
        output.Write(20);
        output.Write(chunks.Count);
        var offset = 24 + chunks.Count * 20;
        foreach (var chunk in chunks)
        {
            output.Write(chunk.Kind);
            output.Write(chunk.Version);
            output.Write(offset);
            output.Write(chunk.Id);
            output.Write(16 + chunk.Body.Length);
            offset += 16 + chunk.Body.Length;
        }
        foreach (var chunk in chunks)
        {
            output.Write(new byte[16]);
            output.Write(chunk.Body);
        }
        List<CryCgaNode> nodes = [new(1, -1, Matrix4x4.Identity, rotation ? -1 : 4, rotation ? 4 : -1, -1) { Name = "root" }];
        if (child)
            nodes.Add(new CryCgaNode(2, 1, Matrix4x4.CreateTranslation(0, 3, 0), -1, -1, -1) { Name = remap ? "root" : "child" });
        var extent = narrow ? new Vector3(0.1f, 1, 1) : Vector3.One;
        var animation = CryCgaAnimation.Read(stream.ToArray(), nodes,
            [new CryCgaRenderBounds(1, new CryBounds(-extent, extent), Matrix4x4.Identity)]);
        return remap ? animation.WithClip(stream.ToArray()) : animation;
    }
}
