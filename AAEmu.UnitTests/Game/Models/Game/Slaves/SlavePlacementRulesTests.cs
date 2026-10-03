using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Slaves;

namespace AAEmu.UnitTests.Game.Models.Game.Slaves;

public sealed class SlavePlacementRulesTests
{
    private static readonly CryBounds LandModel = new(new(-2, -3, 0), new(2, 5, 3));
    private static readonly Vector3 Owner = new(100, 100, 0);
    private static readonly SlavePlacementRequest Request = new(new(100, 110, 0), 1.25f);

    [Test]
    public async Task UnknownDynamicCollisionRejectsOnlyQueriesInsideItsBounds()
    {
        var asset = new CryGeometryAsset(new(new(-1), new(1)), []);
        var instance = new CryGeometryInstance(7, asset, Matrix4x4.CreateTranslation(100, 100, 0));
        var remoteQuery = new CryBounds(new(200, 200, 0), new(205, 205, 1));
        await Assert.That(SlaveManager.QueryPlacementUnits([instance], remoteQuery).ToArray()).IsEmpty();
        var localQuery = new CryBounds(new(99, 99, 0), new(101, 101, 1));
        var scene = new CryGeometryScene(bounds => SlaveManager.QueryPlacementUnits([instance], bounds), _ => true);
        await Assert.That(scene.IntersectBox(new(localQuery.Center, localQuery.HalfSize, Matrix4x4.Identity)))
            .IsEqualTo(CryIntersection.Indeterminate);
        await Assert.That(scene.Raycast(new(95, 100, 0), Vector3.UnitX, 10, out _))
            .IsEqualTo(CryIntersection.Indeterminate);
        await Assert.That(scene.Raycast(new(95, 100, 0), Vector3.UnitX, 10, out _, 7))
            .IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task Range_IsEightyMetresInclusiveInThreeDimensions()
    {
        await Assert.That(SlavePlacementRules.IsWithinRange(Vector3.Zero, new(48, 64, 0))).IsTrue();
        await Assert.That(SlavePlacementRules.IsWithinRange(Vector3.Zero, new(48, 64, 1))).IsFalse();
        await Assert.That(SlavePlacementRules.IsWithinRange(Vector3.Zero, new(0, 0, 80))).IsTrue();
        await Assert.That(SlavePlacementRules.IsWithinRange(Vector3.Zero, new(0, 0, 80.001f))).IsFalse();
        await Assert.That(SlavePlacementRules.IsWithinRange(new(float.NaN), Vector3.Zero)).IsFalse();
    }

    [Test]
    public async Task SavedLocationRange_UsesAuthoredRadiusAndOnlyHorizontalDistance()
    {
        await Assert.That(SlavePlacementRules.IsWithinSavedLocationRange(Vector3.Zero, new(30, 40, 900), 50)).IsTrue();
        await Assert.That(SlavePlacementRules.IsWithinSavedLocationRange(Vector3.Zero, new(30, 40.001f, 0), 50)).IsFalse();
        await Assert.That(SlavePlacementRules.IsWithinSavedLocationRange(Vector3.Zero, new(0, 7, 0), 7)).IsTrue();
        await Assert.That(SlavePlacementRules.IsWithinSavedLocationRange(Vector3.Zero, new(0, 7.001f, 0), 7)).IsFalse();
    }

    [Test]
    public async Task PositionTarget_ReadsNativeBodyAndPreservesSelectedPositionAndYaw()
    {
        var body = new PacketStream();
        body.Write((byte)1).Write(Helpers.ConvertLongX(12000.25f)).Write(Helpers.ConvertLongY(17000.75f))
            .Write(12.5f).Write(1.25f).WriteBc(0).WriteBc(0);
        await Assert.That(body.GetBytes().Length).IsEqualTo(31);
        var input = new PacketStream(body.GetBytes());
        var target = SkillCastTarget.GetByType((SkillCastTargetType)input.ReadByte());
        target.Read(input);
        await Assert.That(input.LeftBytes).IsEqualTo(0);
        await Assert.That(SlavePlacementRequest.TryReadTarget(target, out var request)).IsTrue();
        await Assert.That(request.Position).IsEqualTo(new Vector3(12000.25f, 17000.75f, 12.5f));
        await Assert.That(request.Yaw).IsEqualTo(1.25f);
    }

    [Test]
    public async Task PositionTarget_RejectsWrongKindsReferencesAndNonfiniteFields()
    {
        foreach (var target in new SkillCastTarget[]
        {
            null, new SkillCastUnitTarget(), new SkillCastPosition2Target(), new SkillCastPosition3Target(),
            new SkillCastPositionTarget { ObjId1 = 1 }, new SkillCastPositionTarget { ObjId2 = 2 },
            new SkillCastPositionTarget { PosX = float.NaN }, new SkillCastPositionTarget { PosY = float.PositiveInfinity },
            new SkillCastPositionTarget { PosZ = float.NegativeInfinity }, new SkillCastPositionTarget { PosRot = float.NaN }
        })
            await Assert.That(SlavePlacementRequest.TryReadTarget(target, out _)).IsFalse();
        await Assert.That(SlavePlacementRequest.TryReadTarget(new SkillCastPositionTarget(), out var origin)).IsTrue();
        await Assert.That(origin.Position).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task ScrollLocation_RoundTripsNativePackedPairWithoutChangingOtherDetails()
    {
        var scroll = new SummonSlave { SlaveType = 2, SlaveDbId = 123, SummonLocation = new(12000.25f, 17000.75f, 900) };
        var bytes = new PacketStream();
        scroll.WriteDetails(bytes);
        await Assert.That(bytes.GetBytes().Length).IsEqualTo(29);
        var fields = new PacketStream(bytes.GetBytes());
        fields.ReadBytes(13);
        await Assert.That(fields.ReadInt64()).IsEqualTo(Helpers.ConvertLongX(12000.25f));
        await Assert.That(fields.ReadInt64()).IsEqualTo(Helpers.ConvertLongY(17000.75f));
        await Assert.That(fields.LeftBytes).IsEqualTo(0);
        var restored = new SummonSlave();
        restored.ReadDetails(new PacketStream(bytes.GetBytes()));
        await Assert.That(restored.HasSummonLocation).IsTrue();
        await Assert.That(restored.SummonLocation).IsEqualTo(new Vector3(12000.25f, 17000.75f, 0));
        restored.ClearSummonLocation();
        var cleared = new PacketStream();
        restored.WriteDetails(cleared);
        await Assert.That(cleared.GetBytes()[..13]).IsEquivalentTo(bytes.GetBytes()[..13]);
        await Assert.That(cleared.GetBytes()[13..]).IsEquivalentTo(new byte[16]);
        await Assert.That(restored.HasSummonLocation).IsFalse();
    }

    [Test]
    [Arguments(0f, 0f)]
    [Arguments(0f, 12f)]
    [Arguments(12f, 0f)]
    public async Task ScrollLocation_NativeUnsetPredicateDoesNotEnableRange(float x, float y)
    {
        var scroll = new SummonSlave { SummonLocation = new(x, y, 0) };
        await Assert.That(scroll.HasSummonLocation).IsFalse();
        scroll.ClearSummonLocation();
        await Assert.That(scroll.SummonLocation).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task Land_AcceptsZeroHeightAndKeepsNativeTenMetreRiseBoundary()
    {
        await Assert.That(Check()).IsEqualTo(ErrorMessageType.NoErrorMessage);
        var raised = Request with { Position = Request.Position + Vector3.UnitZ * 10 };
        await Assert.That(Check(request: raised, terrain: 10, surface: 10)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        raised = raised with { Position = raised.Position + Vector3.UnitZ * 0.01f };
        await Assert.That(Check(request: raised, terrain: 10.01f, surface: 10.01f)).IsEqualTo(ErrorMessageType.SlaveSpawnErrorInvalidArea);
    }

    [Test]
    public async Task Surface_RejectsMissingTerrainMissingWaterAndAnotherVerticalLayer()
    {
        await Assert.That(Check(terrain: float.NaN)).IsEqualTo(ErrorMessageType.SlaveSpawnErrorInvalidArea);
        await Assert.That(Check(boat: true, surface: float.NaN)).IsEqualTo(ErrorMessageType.SlaveSpawnErrorInvalidArea);
        await Assert.That(Check(terrain: 20, surface: 20)).IsEqualTo(ErrorMessageType.SlaveSpawnErrorInvalidArea);
        await Assert.That(Check(request: Request with { Position = new(100, 110, 1) })).IsEqualTo(ErrorMessageType.SlaveSpawnErrorInvalidArea);
    }

    [Test]
    [Arguments(false, 0xf)]
    [Arguments(true, 0x10f)]
    public async Task Geometry_UsesFullModelBoundsAndNativeEntityMask(bool boat, int expectedMask)
    {
        CryBox? queried = null;
        var mask = 0;
        var model = new CryBounds(new(-2, -4, -3), new(6, 8, 5));
        var result = Check(boat: boat, model: model, intersect: (box, entities) =>
        {
            queried = box;
            mask = entities;
            return CryIntersection.Clear;
        });
        await Assert.That(result).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(mask).IsEqualTo(expectedMask);
        await Assert.That(queried!.Center).IsEqualTo(Request.Position + new Vector3(2, 2, 1));
        await Assert.That(queried.HalfSize).IsEqualTo(new Vector3(4, 6, 4));
        await Assert.That(queried.Orientation).IsEqualTo(Matrix4x4.CreateRotationZ(Request.Yaw));
    }

    [Test]
    [Arguments(CryIntersection.Intersects)]
    [Arguments(CryIntersection.Indeterminate)]
    public async Task Geometry_RejectsBlockedOrUnknownBoxAndRay(CryIntersection result)
    {
        await Assert.That(Check(intersect: (_, _) => result)).IsEqualTo(ErrorMessageType.SlaveSpawnShipNeedMoreSpace);
        var boxCalled = false;
        var error = Check(intersect: (_, _) => { boxCalled = true; return CryIntersection.Clear; },
            trace: (origin, end) =>
            {
                if (origin != Owner + Vector3.UnitZ || end != Request.Position)
                    throw new InvalidOperationException("The native ray uses caster height plus one metre.");
                return result;
            });
        await Assert.That(error).IsEqualTo(ErrorMessageType.SlaveSpawnErrorInvalidArea);
        await Assert.That(boxCalled).IsFalse();
    }

    [Test]
    public async Task Boat_RejectsShallowHullContactAndAllowsZeroSeaLevel()
    {
        var hull = new CryBounds(new(-2, -3, -2), new(2, 3, 4));
        CryIntersection Intersect(CryBox box, int mask)
        {
            var floor = new CryBox(new(100, 110, -2), new(20, 20, 0.5f), Matrix4x4.Identity);
            return CryGeometryQueries.IntersectBox(new CryGeometryPart(floor, Matrix4x4.Identity,
                CryGeometryLayerRules.Solid, "", "seabed"), Matrix4x4.Identity, box, Matrix4x4.Identity);
        }
        await Assert.That(Check(boat: true, terrain: -1.5f, model: hull, intersect: Intersect))
            .IsEqualTo(ErrorMessageType.SlaveSpawnShipNeedMoreSpace);
        await Assert.That(Check(boat: true, terrain: -20, model: hull)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(boat: true, terrain: 1, model: hull)).IsEqualTo(ErrorMessageType.SlaveSpawnErrorInvalidArea);
    }

    [Test]
    public async Task Ray_SkipsLivingActorsAndOnlyTheAttachedObjectWhileOverlapKeepsBoth()
    {
        var bounds = new CryBounds(-Vector3.One, Vector3.One);
        var asset = new CryGeometryAsset(bounds,
            [new CryGeometryPart(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity),
                Matrix4x4.Identity, CryGeometryLayerRules.Solid, "", "body")]);
        var actor = new CryGeometryInstance(1, asset, Matrix4x4.Identity, 8);
        var attached = new CryGeometryInstance(2, asset, Matrix4x4.CreateTranslation(3, 0, 0), 1);
        var scene = new CryGeometryScene(_ => [actor, attached], _ => true);
        await Assert.That(scene.Raycast(new(-3, 0, 0), Vector3.UnitX, 10, out var hit))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(hit.ObjectId).IsEqualTo(2u);
        await Assert.That(scene.Raycast(new(-3, 0, 0), Vector3.UnitX, 10, out _, 2))
            .IsEqualTo(CryIntersection.Clear);
        await Assert.That(scene.Raycast(new(-3, 0, 0), Vector3.UnitX, 10, out _, 3))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(scene.IntersectBox(new(Vector3.Zero, Vector3.One, Matrix4x4.Identity), 0xf))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(scene.IntersectBox(new(new(3, 0, 0), Vector3.One, Matrix4x4.Identity), 0xf))
            .IsEqualTo(CryIntersection.Intersects);
    }

    private static ErrorMessageType Check(SlavePlacementRequest? request = null, bool boat = false,
        float terrain = 0, float surface = 0, CryBounds? model = null,
        Func<CryBox, int, CryIntersection> intersect = null, Func<Vector3, Vector3, CryIntersection> trace = null) =>
        SlavePlacementRules.CheckGeometry(Owner, request ?? Request, model ?? LandModel, boat, terrain, surface,
            intersect ?? ((_, _) => CryIntersection.Clear), trace ?? ((_, _) => CryIntersection.Clear));
}
