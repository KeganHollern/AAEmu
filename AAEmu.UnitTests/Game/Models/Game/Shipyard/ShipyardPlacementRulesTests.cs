using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Shipyard;

namespace AAEmu.UnitTests.Game.Models.Game.Shipyards;

public sealed class ShipyardPlacementRulesTests
{
    private static readonly CryBounds Model = new(new(-4, -8, -20), new(6, 12, 10));
    private static readonly Vector3 Position = new(100, 100, 109.9f);

    [Test]
    public async Task Range_IsInclusiveAndUsesAllThreeAxes()
    {
        await Assert.That(ShipyardPlacementRules.IsWithinRange(Vector3.Zero, new(27, 36, 0))).IsTrue();
        await Assert.That(ShipyardPlacementRules.IsWithinRange(Vector3.Zero, new(27, 36, 0.1f))).IsFalse();
        await Assert.That(ShipyardPlacementRules.IsWithinRange(Vector3.Zero, new(0, 0, 45))).IsTrue();
        await Assert.That(ShipyardPlacementRules.IsWithinRange(Vector3.Zero, new(0, 0, 45.001f))).IsFalse();
        await Assert.That(ShipyardPlacementRules.IsWithinRange(new(float.NaN, 0, 0), Vector3.Zero)).IsFalse();
    }

    [Test]
    public async Task Bounds_MustBeFiniteAndOrdered()
    {
        await Assert.That(ShipyardPlacementRules.ValidBounds(Model)).IsTrue();
        await Assert.That(ShipyardPlacementRules.ValidBounds(new(Vector3.One, Vector3.Zero))).IsFalse();
        await Assert.That(ShipyardPlacementRules.ValidBounds(new(Vector3.Zero, Vector3.Zero))).IsFalse();
        await Assert.That(ShipyardPlacementRules.ValidBounds(new(Vector3.Zero, new(1, 1, float.PositiveInfinity)))).IsFalse();
    }

    [Test]
    public async Task Water_AcceptsZeroSeaLevelAndRejectsForgedHeight()
    {
        await Assert.That(ShipyardPlacementRules.IsOnWater(new(100, 100, 9.9f), 0)).IsTrue();
        await Assert.That(ShipyardPlacementRules.IsOnWater(Position, 100)).IsTrue();
        await Assert.That(ShipyardPlacementRules.IsOnWater(Position + Vector3.UnitZ, 100)).IsFalse();
        await Assert.That(ShipyardPlacementRules.IsOnWater(Position - Vector3.UnitZ, 100)).IsFalse();
        await Assert.That(ShipyardPlacementRules.IsOnWater(Position, float.NaN)).IsFalse();
    }

    [Test]
    public async Task NativeBoxes_KeepDistinctHalfScalesAndForwardCentres()
    {
        var shipyard = ShipyardPlacementRules.ShipyardBox(Model, Vector3.Zero, 0, 26);
        var water = ShipyardPlacementRules.ClearanceBox(Model, Vector3.Zero, 0, 26);
        await Assert.That(shipyard.HalfSize).IsEqualTo(new Vector3(31, 25, 15));
        await Assert.That(shipyard.Center).IsEqualTo(new Vector3(1, 17, -5));
        await Assert.That(water.HalfSize).IsEqualTo(new Vector3(29, 21, 10.3f));
        await Assert.That(water.Center).IsEqualTo(new Vector3(0.6f, 16.2f, -3));
        var rotated = ShipyardPlacementRules.ClearanceBox(Model, Vector3.Zero, MathF.PI / 2, 26);
        await Assert.That(Vector3.Distance(rotated.Center, new(-16.2f, 0.6f, -3)) < 0.00001f).IsTrue();
        var living = ShipyardPlacementRules.LivingBox(Model, Vector3.Zero, MathF.PI / 2);
        await Assert.That(living.Center).IsEqualTo(shipyard.Center);
        await Assert.That(living.HalfSize).IsEqualTo(Model.HalfSize);
    }

    [Test]
    public async Task Neighbor_ClearanceUsesItsRotatedModelAndTouchingFaces()
    {
        var box = ShipyardPlacementRules.ShipyardBox(Model, Position, 0, 26);
        var neighbor = ShipyardPlacementRules.ShipyardBox(Model, Position + new Vector3(36, 0, 0), 0, 0);
        await Assert.That(ShipyardPlacementRules.Overlaps(box, neighbor)).IsTrue();
        await Assert.That(ShipyardPlacementRules.Overlaps(box, neighbor with { Center = neighbor.Center + new Vector3(0.01f, 0, 0) })).IsFalse();
        await Assert.That(ShipyardPlacementRules.Overlaps(box, neighbor with { Center = neighbor.Center + new Vector3(0, 0, 31) })).IsFalse();
    }

    [Test]
    [Arguments(CryIntersection.Intersects)]
    [Arguments(CryIntersection.Indeterminate)]
    public async Task Geometry_RejectsLivingUnitAndUnknownShape(CryIntersection collision)
    {
        var calls = new List<int>();
        var error = Check((_, mask) => { calls.Add(mask); return collision; });
        await Assert.That(error).IsEqualTo(ErrorMessageType.CraftLocatingUnitIsTooCloseToUnit);
        await Assert.That(calls.SequenceEqual([8])).IsTrue();
    }

    [Test]
    [Arguments(CryIntersection.Intersects)]
    [Arguments(CryIntersection.Indeterminate)]
    public async Task Geometry_RejectsStaticObjectsAndShallowTerrain(CryIntersection collision)
    {
        var calls = new List<int>();
        var error = Check((_, mask) => { calls.Add(mask); return mask == 8 ? CryIntersection.Clear : collision; });
        await Assert.That(error).IsEqualTo(ErrorMessageType.CraftLocatingUnitIsNotOnTheWaterOrDeepWater);
        await Assert.That(calls.SequenceEqual([8, 0x10d])).IsTrue();
    }

    [Test]
    public async Task Geometry_RejectsNeighborBeforeOtherQueriesAndAcceptsClearWater()
    {
        var queried = false;
        var error = ShipyardPlacementRules.CheckGeometry(Model, Position, 0, 26, 100,
            [ShipyardPlacementRules.ShipyardBox(Model, Position, 0, 0)], (_, _) => { queried = true; return CryIntersection.Clear; });
        await Assert.That(error).IsEqualTo(ErrorMessageType.CraftLocatingUnitIsTooCloseToOther);
        await Assert.That(queried).IsFalse();
        await Assert.That(Check((_, _) => CryIntersection.Clear)).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task EntityMasks_DistinguishLivingAndStaticOverlaps()
    {
        var part = new CryGeometryPart(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity),
            Matrix4x4.Identity, CryGeometryLayerRules.Solid, "", "house");
        var asset = new CryGeometryAsset(new(-Vector3.One, Vector3.One), [part]);
        var scene = new CryGeometryScene(_ => [new(1, asset, Matrix4x4.Identity, 1)], _ => true);
        var query = new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity);
        await Assert.That(scene.IntersectBox(query, 8)).IsEqualTo(CryIntersection.Clear);
        await Assert.That(scene.IntersectBox(query, 0x10d)).IsEqualTo(CryIntersection.Intersects);
        await Assert.That(scene.IntersectBox(query)).IsEqualTo(CryIntersection.Intersects);
    }

    private static ErrorMessageType Check(Func<CryBox, int, CryIntersection> intersect) =>
        ShipyardPlacementRules.CheckGeometry(Model, Position, 0, 26, 100, [], intersect);
}
