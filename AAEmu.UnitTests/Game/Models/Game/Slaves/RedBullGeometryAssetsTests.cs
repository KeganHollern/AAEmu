using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Slaves;

namespace AAEmu.UnitTests.Game.Models.Game.Slaves;

public sealed class RedBullGeometryAssetsTests
{
    private const string AssetRoot = "game/objects/env/06_unit/05_carriage/";
    private const string PrefabRoot = "prefab://prefabs/speedcar.xml/speedcar.speedcar_";

    [Test]
    public async Task Candidate_NormalBodyAndHoodUsePreservedBoundsAndIndependentMaterial()
    {
        var source = OpenCandidate();
        try
        {
            var resolver = new CryGeometryResolver(path => source.FileExists(path) ? source.GetFileStream(path) : null);
            foreach (var part in new[] { "body", "side" })
            {
                var model = resolver.Load(PrefabRoot + part + "_redbull");
                var preservedRig = resolver.Load(AssetRoot + "speedcar_" + part + ".chr");

                await Assert.That(model.HasModelBounds).IsTrue();
                await Assert.That(SlavePlacementRules.ValidBounds(model.Bounds)).IsTrue();
                await AssertBoundsNear(model.Bounds, preservedRig.Bounds);
                await Assert.That(model.Parts.Count).IsGreaterThan(0);
                await Assert.That(model.Parts.All(p => p.MaterialPath == AssetRoot + "speedcar_redbull")).IsTrue();
                await Assert.That(model.PoseRequirements.Any(p => p.Playing && p.Physicalized && p.AffectsCollision)).IsFalse();
                Console.WriteLine($"Red Bull {part}: bounds {model.Bounds.Min} to {model.Bounds.Max}, collision parts {model.Parts.Count}.");
                var points = model.Parts.SelectMany(p => p.Shape is CryTriangleMesh mesh ?
                    mesh.Vertices.Select(v => Vector3.Transform(v, p.Transform)) : []).ToArray();
                if (points.Length > 0)
                {
                    Console.WriteLine($"Triangle collision {part}: {points.Aggregate(Vector3.Min)} to {points.Aggregate(Vector3.Max)}; " +
                        $"contained in skeleton bounds: {points.All(model.Bounds.Contains)}.");
                    var collisionBounds = HousingGeometryAssets.CollisionBounds(model, Matrix4x4.Identity);
                    await Assert.That(points.All(collisionBounds.Contains)).IsTrue();
                    // A nearby-object query must keep collision outside the native skeleton envelope.
                    var outside = points.First(p => Vector3.Distance(p,
                        Vector3.Clamp(p, model.Bounds.Min, model.Bounds.Max)) > 0.01f);
                    var query = new CryBounds(outside - new Vector3(0.001f), outside + new Vector3(0.001f));
                    await Assert.That(model.Bounds.Intersects(query)).IsFalse();
                    var instance = new CryGeometryInstance(1427, model, Matrix4x4.Identity);
                    await Assert.That(SlaveManager.QueryPlacementUnits([instance], query)).HasSingleItem();
                }
            }
        }
        finally { source.Close(); }
    }

    [Test]
    [Arguments("body", "steering", "speedcar_body/steering.caf")]
    [Arguments("body", "fist_ac_plank_up", "speedcar_body/speedcar_body_opening.caf")]
    [Arguments("side", "spread", "speedcar_side/speedcar_side_opening.caf")]
    [Arguments("side", "fold", "speedcar_side/speedcar_side_closing.caf")]
    public async Task Candidate_RenamedCalResolvesNativeClipAndPrefabPhase(string part, string clip, string animation)
    {
        var source = OpenCandidate();
        try
        {
            var opened = new List<string>();
            Stream Open(string path)
            {
                if (!source.FileExists(path))
                    return null;
                opened.Add(path);
                return source.GetFileStream(path);
            }
            var resolver = new CryGeometryResolver(Open);
            var modelPath = AssetRoot + "speedcar_redbull_" + part;
            var direct = resolver.LoadCharacterPose(modelPath + ".chr", clip, 0.5, false);
            var prefab = resolver.LoadAnimationPose(PrefabRoot + part + "_redbull", clip, 0.5, false);

            await Assert.That(opened).Contains(modelPath + ".cal");
            await Assert.That(opened).Contains("game/animations/env/speedcar/" + animation);
            await Assert.That(direct.HasModelBounds).IsTrue();
            await Assert.That(SlavePlacementRules.ValidBounds(direct.Bounds)).IsTrue();
            await AssertBoundsNear(prefab.Bounds, direct.Bounds);
            await Assert.That(prefab.Parts.Select(p => p.Transform)).IsEquivalentTo(direct.Parts.Select(p => p.Transform));
        }
        finally { source.Close(); }
    }

    private static async Task AssertBoundsNear(CryBounds actual, CryBounds expected)
    {
        // Prefab composition converts min/max to center/half-size and back.
        await Assert.That(Vector3.Distance(actual.Min, expected.Min)).IsLessThan(0.00001f);
        await Assert.That(Vector3.Distance(actual.Max, expected.Max)).IsLessThan(0.00001f);
    }

    private static ClientSource OpenCandidate()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_REDBULL_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_REDBULL_GAME_PAK to the explicit Red Bull candidate archive.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        if (!source.Open())
            throw new InvalidOperationException("Cannot open the Red Bull candidate archive.");
        return source;
    }
}
