using System.Numerics;
using System.Text;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryPhaseAnimationTests
{
    [Test]
    public async Task ExactClient_CgaPhase_UsesNamedDoorClipAndKeepsMissingNamePose()
    {
        var source = OpenSource();
        try
        {
            var resolver = new CryGeometryResolver(path => source.FileExists(path) ? source.GetFileStream(path) : null);
            const string uri = "cga://objects/env/01_nuia/001_ndeco/housing/ndeco_housing_bookshelf01.cga";
            var expected = resolver.LoadCgaPose(uri, "opened", 0.5, false);
            var actual = resolver.LoadAnimationPose(uri, "opened", 0.5, false);
            await Assert.That(actual.Bounds).IsEqualTo(expected.Bounds);
            await Assert.That(actual.Parts.Select(part => part.Transform).SequenceEqual(expected.Parts.Select(part => part.Transform))).IsTrue();
            var missing = resolver.LoadAnimationPose(uri, "missing_phase_clip", 0.5, false);
            await Assert.That(missing.Bounds).IsEqualTo(resolver.LoadPose(uri, 0.5).Bounds);
        }
        finally { source.Close(); }
    }

    [Test]
    public async Task ExactClient_PrefabPhase_StartsStoppedCharacterAndKeepsObjectTransform()
    {
        var source = OpenSource();
        try
        {
            const string model = "objects/env/01_nuia/001_ndeco/making/ndeco_making_sewing01.chr";
            var xml = Encoding.UTF8.GetBytes($"<PrefabsLibrary><Prefab Name='sewing'><Objects><Object Type='Entity' EntityClass='AnimObject' Pos='10,20,30'><Properties object_Model='{model}'><Animation Animation='Default' bPlaying='0'/></Properties></Object></Objects></Prefab></PrefabsLibrary>");
            var resolver = new CryGeometryResolver(path => path == "game/prefabs/test.xml" ? new MemoryStream(xml) :
                source.FileExists(path) ? source.GetFileStream(path) : null);
            var expected = resolver.LoadCharacterPose(model, "operate", 0.5, true);
            var actual = resolver.LoadAnimationPose("prefab://prefabs/test.xml/sewing", "operate", 0.5, true);
            var transform = Matrix4x4.CreateTranslation(10, 20, 30);
            await Assert.That(actual.Bounds).IsEqualTo(expected.Bounds.Transform(transform));
            await Assert.That(actual.Parts.Count).IsGreaterThan(0);
            await Assert.That(actual.Parts.Select(part => part.Transform).SequenceEqual(expected.Parts.Select(part => part.Transform * transform))).IsTrue();
            await Assert.That(actual.PoseRequirements.Count).IsEqualTo(0);
        }
        finally { source.Close(); }
    }

    private static ClientSource OpenSource()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 phase animation check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        if (!source.Open())
            throw new InvalidOperationException("Cannot open the authored game_pak fixture.");
        return source;
    }
}
