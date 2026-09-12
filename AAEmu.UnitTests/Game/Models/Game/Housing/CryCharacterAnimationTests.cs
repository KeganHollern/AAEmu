using System.Numerics;
using System.Text;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryCharacterAnimationTests
{
    [Test]
    public async Task Sample_LocalTracks_ApplyParentPoseAndClipStartFrame()
    {
        var clip = CryCharacterAnimation.Read(Clip());
        var pose = clip.Sample(BindAsset(), 0.5, false);
        await Assert.That(Vector3.Distance(pose.Parts[0].Transform.Translation, new Vector3(1, 1, 0))).IsLessThan(0.00001f);
        await Assert.That(pose.PoseRequirements.Any(requirement => requirement.AffectsCollision)).IsFalse();
    }

    [Test]
    public async Task Sample_LoopingAndLastKey_UseClipDuration()
    {
        var clip = CryCharacterAnimation.Read(Clip());
        var bind = BindAsset();
        var repeat = clip.Sample(bind, clip.DurationSeconds + 0.5, true);
        var initial = clip.Sample(bind, 0.5, true);
        var end = clip.Sample(bind, clip.DurationSeconds + 10, false);
        await Assert.That(Vector3.Distance(repeat.Parts[0].Transform.Translation, initial.Parts[0].Transform.Translation)).IsLessThan(0.00001f);
        await Assert.That(Vector3.Distance(end.Parts[0].Transform.Translation, Vector3.UnitX)).IsLessThan(0.00001f);
    }

    [Test]
    [Arguments(5)]
    [Arguments(8)]
    public async Task Read_AuthoredSmallTreeIdentity_UsesCorrectComponentPacking(int format)
    {
        var clip = CryCharacterAnimation.Read(Clip(format));
        var pose = clip.Sample(BindAsset(), 0, false);
        await Assert.That(Vector3.Distance(pose.Parts[0].Transform.Translation, Vector3.UnitX)).IsLessThan(0.0001f);
    }

    [Test]
    public async Task Read_NonmonotonicTimes_RejectsInvalidTrack()
    {
        await Assert.That(() => CryCharacterAnimation.Read(Clip(1, true))).Throws<InvalidDataException>();
    }

    private static CryGeometryAsset BindAsset()
    {
        var sphere = new CrySphere(Vector3.Zero, 0.1f);
        return new CryGeometryAsset(new CryBounds(-Vector3.One, Vector3.One),
            [new CryGeometryPart(sphere, Matrix4x4.CreateTranslation(Vector3.UnitX), 0x1000, "", "child") { BoneIndex = 1 }])
        {
            CharacterBones =
            [
                new CryCharacterBone(0, 23, "root", -1, Matrix4x4.Identity, null, 0),
                new CryCharacterBone(1, 24, "child", 0, Matrix4x4.CreateTranslation(Vector3.UnitX), sphere, 0)
            ],
            HasAnimatedCollision = true,
            PoseRequirements = [new CryGeometryPoseRequirement("model.chr", "Default", Matrix4x4.Identity, "Default", true, true)]
        };
    }

    private static byte[] Clip(int rotationFormat = 1, bool badTimes = false)
    {
        using var timingStream = new MemoryStream();
        using (var writer = new BinaryWriter(timingStream, Encoding.UTF8, true))
        {
            writer.Write(1f / 4800);
            writer.Write(160);
            writer.Write(new byte[32]);
            writer.Write(100);
            writer.Write(130);
        }
        using var trackStream = new MemoryStream();
        using (var writer = new BinaryWriter(trackStream, Encoding.UTF8, true))
        {
            writer.Write(23u);
            writer.Write((ushort)2);
            writer.Write((ushort)2);
            writer.Write((byte)rotationFormat);
            writer.Write((byte)1);
            writer.Write((byte)2);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)0);
            for (var i = 0; i < 2; i++)
            {
                if (rotationFormat == 1)
                    foreach (var value in new[] { 0f, 0, (float)i, 1 - i })
                        writer.Write(value);
                else
                    writer.Write(Convert.FromHexString(rotationFormat == 5 ? "0040002000D0" : "FFFFEFFFFFFDFFDF"));
            }
            writer.Write((ushort)100);
            writer.Write((ushort)(badTimes ? 100 : 130));
            foreach (var value in new[] { 0f, 0, 0, 2, 0, 0 })
                writer.Write(value);
        }
        var chunks = new[] { (Kind: 0xcccc000eu, Version: 0x918, Data: timingStream.ToArray()),
            (Kind: 0xcccc000du, Version: 0x829, Data: trackStream.ToArray()) };
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream);
        output.Write(Encoding.ASCII.GetBytes("CryTek\0\0"));
        output.Write(0xffff0000);
        output.Write(0x745);
        output.Write(20);
        output.Write(chunks.Length);
        var offset = 24 + chunks.Length * 20;
        for (var i = 0; i < chunks.Length; i++)
        {
            output.Write(chunks[i].Kind);
            output.Write(chunks[i].Version);
            output.Write(offset);
            output.Write(i + 1);
            output.Write(chunks[i].Data.Length + 16);
            offset += chunks[i].Data.Length + 16;
        }
        foreach (var chunk in chunks)
        {
            output.Write(new byte[16]);
            output.Write(chunk.Data);
        }
        return stream.ToArray();
    }
}
