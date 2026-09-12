using System.Numerics;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryCharacterBoundsTests
{
    [Test]
    public async Task FromPose_PreviewIncludesEveryBone_ActiveBoundsUseSkinPalette()
    {
        Matrix4x4[] pose = [Matrix4x4.CreateTranslation(-10, 0, 0), Matrix4x4.CreateTranslation(1, 2, 3)];
        var preview = CryCharacterBounds.FromPose(pose);
        var active = CryCharacterBounds.FromPose(pose, [1]);
        await Assert.That(preview.Min).IsEqualTo(new Vector3(-10, 0, 0));
        await Assert.That(preview.Max).IsEqualTo(new Vector3(1, 2, 3));
        await Assert.That(active.Min).IsEqualTo(new Vector3(0.8f, 1.8f, 2.8f));
        await Assert.That(active.Max).IsEqualTo(new Vector3(1.2f, 2.2f, 3.2f));
    }

    [Test]
    public async Task FromPose_EmptyOrInvalidBounds_UseNativeFallback()
    {
        var empty = CryCharacterBounds.FromPose([], []);
        var extreme = CryCharacterBounds.FromPose([Matrix4x4.CreateTranslation(13001, 0, 0)]);
        await Assert.That(empty).IsEqualTo(new CryBounds(new Vector3(-2), new Vector3(2)));
        await Assert.That(extreme).IsEqualTo(empty);
    }

    [Test]
    public async Task ReadSubsetBones_UsesFixedPaletteStride_ExcludesUnusedIds()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2u);
        writer.Write(2);
        writer.Write(new byte[8 + 72]);
        for (var subset = 0; subset < 2; subset++)
        {
            writer.Write(subset + 1);
            for (var i = 0; i < 128; i++)
                writer.Write((ushort)(i < subset + 1 ? subset + i + 3 : 99));
        }
        var bones = CryCharacterBounds.ReadSubsetBones(stream.ToArray());
        await Assert.That(bones.SequenceEqual([3, 4, 5])).IsTrue();
        await Assert.That(() => CryCharacterBounds.ReadSubsetBones(stream.ToArray()[..^1])).Throws<InvalidDataException>();
    }
}
