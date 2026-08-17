using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Physics;
using AAEmu.Game.Physics.Forces;

using Jitter2.LinearMath;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public class WaterBodiesNativeRiverTests
{
    [Test]
    public async Task WaterObjectVolumeType_UsesCryEngineSerializedValues()
    {
        await Assert.That((int)WaterObjectVolumeType.Unknown).IsEqualTo(0);
        await Assert.That((int)WaterObjectVolumeType.Ocean).IsEqualTo(1);
        await Assert.That((int)WaterObjectVolumeType.Area).IsEqualTo(2);
        await Assert.That((int)WaterObjectVolumeType.River).IsEqualTo(3);
    }

    [Test]
    public async Task AddFromCellData_RiverContour_UsesNativeSpeedAndDirection()
    {
        var contour = CreateRiverContour();
        var riverObject = CreateWaterObject(WaterObjectVolumeType.River, 2f, contour);
        var water = CreateWaterBodiesWithObject(riverObject);

        var area = water.Areas.Single();
        await Assert.That(area.AreaType).IsEqualTo(WaterBodyAreaType.River);
        await Assert.That(area.Speed).IsEqualTo(2f);
        await Assert.That(area.Points.Count).IsEqualTo(contour.Count);

        var surface = water.GetWaterSurface(new Vector3(2f, 5f, 10f), out var flow);
        await Assert.That(surface).IsEqualTo(10f).Within(0.001f);
        await Assert.That(flow.Length()).IsEqualTo(2f).Within(0.02f);
        await Assert.That(flow.X).IsGreaterThan(1.9f);
        await Assert.That(flow.Y).IsGreaterThanOrEqualTo(0f);
    }

    [Test]
    public async Task AddFromCellData_CurvedRiver_InterpolatesLocalNativeFlow()
    {
        var riverObject = CreateWaterObject(WaterObjectVolumeType.River, 2f, CreateRiverContour());
        var water = CreateWaterBodiesWithObject(riverObject);

        _ = water.GetWaterSurface(new Vector3(17f, 12f, 10f), out var downstreamFlow);

        // The serialized render quad points east, but the physics contour bends north-east here.
        // CryPhysics interpolates the per-bank tangent vectors rather than using one volume-wide axis.
        await Assert.That(downstreamFlow.Length()).IsEqualTo(2f).Within(0.05f);
        await Assert.That(downstreamFlow.X).IsEqualTo(1.834f).Within(0.02f);
        await Assert.That(downstreamFlow.Y).IsEqualTo(0.772f).Within(0.02f);
    }

    [Test]
    public async Task AddFromCellData_OddRiverContour_PreservesPhysicalAreaAndFlow()
    {
        List<Vector3> contour =
        [
            new(0f, 0f, 10f),
            new(10f, 0f, 10f),
            new(20f, 5f, 10f),
            new(10f, 10f, 10f),
            new(0f, 10f, 10f)
        ];
        var riverObject = CreateWaterObject(WaterObjectVolumeType.River, 2f, contour);
        var water = CreateWaterBodiesWithObject(riverObject);

        _ = water.GetWaterSurface(new Vector3(2f, 5f, 10f), out var flow);

        await Assert.That(water.Areas.Single().Points.Count).IsEqualTo(5);
        await Assert.That(flow.Length()).IsEqualTo(2f).Within(0.02f);
        await Assert.That(flow.X).IsGreaterThan(1.9f);
    }

    [Test]
    public async Task AddFromCellData_AreaWithSpeed_DoesNotCreateRiverFlow()
    {
        List<Vector3> contour =
        [
            new(0f, 0f, 10f),
            new(20f, 0f, 10f),
            new(20f, 20f, 10f),
            new(0f, 20f, 10f)
        ];
        var areaObject = CreateWaterObject(WaterObjectVolumeType.Area, 10f, contour);
        var water = CreateWaterBodiesWithObject(areaObject, minIngestFootprint: 0f);

        var area = water.Areas.Single();
        await Assert.That(area.AreaType).IsEqualTo(WaterBodyAreaType.Polygon);
        await Assert.That(area.Speed).IsEqualTo(0f);

        _ = water.GetWaterSurface(new Vector3(10f, 10f, 10f), out var flow);
        await Assert.That(flow).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task AddFromCellData_TiltedFogPlane_ReturnsLocalSurfaceHeight()
    {
        List<Vector3> contour =
        [
            new(0f, 0f, 0f),
            new(20f, 0f, 0f),
            new(20f, 20f, 0f),
            new(0f, 20f, 0f)
        ];
        var areaObject = CreateWaterObject(WaterObjectVolumeType.Area, 0f, contour,
            fogPlaneNormal: new Vector3(0f, -0.1f, 1f), fogPlaneD: -10f);
        var water = CreateWaterBodiesWithObject(areaObject, minIngestFootprint: 0f);

        var surface = water.GetWaterSurface(new Vector3(10f, 10f, 11f), out _);

        await Assert.That(surface).IsEqualTo(11f).Within(0.001f);
    }

    [Test]
    public async Task WaterQueries_OverlappingAreas_SelectSmallestAreaInsteadOfAveraging()
    {
        var water = new WaterBodies { OceanLevel = 0f };
        var large = water.AddSquareArea("Large", new Vector3(50f, 50f, 10f), 80f, 5f);
        large.FlowAxis = Vector2.UnitX;
        large.FlowSpeedAbs = 1f;
        large.FlowSpeedSigned = 1f;

        var small = water.AddSquareArea("Small", new Vector3(50f, 50f, 10f), 20f, 5f);
        small.FlowAxis = Vector2.UnitY;
        small.FlowSpeedAbs = 3f;
        small.FlowSpeedSigned = 3f;

        var isWater = water.IsWater(new Vector3(50f, 50f, 9f), out var flow);

        await Assert.That(isWater).IsTrue();
        await Assert.That(flow.X).IsEqualTo(0f).Within(0.001f);
        await Assert.That(flow.Y).IsEqualTo(3f).Within(0.001f);
    }

    [Test]
    public async Task GetWaterSurface_LocalAreaBelowOceanLevel_TakesPriorityOverOceanFallback()
    {
        var water = new WaterBodies { OceanLevel = 100f };
        _ = water.AddSquareArea("Local", new Vector3(50f, 50f, 50f), 20f, 5f);

        var surface = water.GetWaterSurface(new Vector3(50f, 50f, 49f), out _);

        await Assert.That(surface).IsEqualTo(50f).Within(0.001f);
    }

    [Test]
    public async Task ComposeWaterRelativeVelocity_IncludesCurrentInRigidBodyVelocity()
    {
        var bodyVelocity = new JVector(3f, 0f, 0f);
        var waterVelocity = new JVector(3f, 0f, 0f);

        var result = ShipController.ComposeWaterRelativeVelocity(bodyVelocity, waterVelocity,
            forwardX: 0f, forwardZ: 1f, propulsionSpeed: 2f, lateralDamping: 0.5f, verticalDamping: 1f);

        await Assert.That(result.X).IsEqualTo(3f).Within(0.001f);
        await Assert.That(result.Z).IsEqualTo(2f).Within(0.001f);
    }

    [Test]
    public async Task CalculateWaterResistanceForce_DampsVelocityRelativeToFlow()
    {
        var force = Buoyancy.CalculateWaterResistanceForce(new JVector(2f, 0f, 0f),
            mass: 100f, resistancePerSecond: 1f, timeStep: 0.1f);
        var noRelativeForce = Buoyancy.CalculateWaterResistanceForce(JVector.Zero,
            mass: 100f, resistancePerSecond: 1f, timeStep: 0.1f);

        await Assert.That(force.X).IsLessThan(0f);
        await Assert.That(force.Y).IsEqualTo(0f);
        await Assert.That(noRelativeForce).IsEqualTo(JVector.Zero);
    }

    [Test]
    public async Task ResolveAllPairs_MultipleShipsInCurrent_DoesNotFoldCurrentIntoPropulsionSpeed()
    {
        using var physicsWorld = new Jitter2.World();
        var shipModel = new ShipModelV1 { MassBoxSizeZ = 2f };
        var first = CreateFloatingSlaveInCurrent(physicsWorld, shipModel, 0f);
        var second = CreateFloatingSlaveInCurrent(physicsWorld, shipModel, 100f);

        new ShipShipInteraction().ResolveAllPairs([first, second], TimeSpan.FromMilliseconds(16));

        await Assert.That(first.Speed).IsEqualTo(0f).Within(0.001f);
        await Assert.That(second.Speed).IsEqualTo(0f).Within(0.001f);
        await Assert.That(first.RigidBody.Velocity.Z).IsEqualTo(3f).Within(0.001f);
    }

    [Test]
    public async Task EncodeMovementVelocity_HighNativeCurrent_SaturatesWithoutSignReversal()
    {
        var positive = PhysicsManager.EncodeMovementVelocity(20f);
        var negative = PhysicsManager.EncodeMovementVelocity(-20f);

        await Assert.That(positive).IsEqualTo(short.MaxValue);
        await Assert.That(negative).IsEqualTo(short.MinValue);
        await Assert.That(PhysicsManager.EncodeMovementVelocity(float.NaN)).IsEqualTo((short)0);
    }

    [Test]
    public async Task GetEffectiveWaterVelocity_UsesTransformedHullBottomWithMassCenterOffset()
    {
        using var physicsWorld = new Jitter2.World();
        var aboveWater = CreateSlaveWithBuiltHull(physicsWorld, massCenterZ: 4f, bodyHeight: 10f);
        var touchingWater = CreateSlaveWithBuiltHull(physicsWorld, massCenterZ: -4f, bodyHeight: 14f);

        var aboveVelocity = ShipController.GetEffectiveWaterVelocity(aboveWater, aboveWater.RigidBody);
        var touchingVelocity = ShipController.GetEffectiveWaterVelocity(touchingWater, touchingWater.RigidBody);

        await Assert.That(aboveVelocity).IsEqualTo(JVector.Zero);
        await Assert.That(touchingVelocity.Z).IsEqualTo(3f).Within(0.001f);
    }

    private static WaterBodies CreateWaterBodiesWithObject(ObjectDataType11Water waterObject,
        float minIngestFootprint = WaterBodies.MinWaterBboxAreaSquareMeters)
    {
        var template = new WorldTemplate
        {
            Name = "test_world",
            OceanLevel = 0f,
            CellX = 1,
            CellY = 1
        };
        var cell = new WorldCell(0, 0, template)
        {
            LoadedObjectDat = new ObjectsFile("test") { PrefabsList = [waterObject] }
        };
        template.Cells = new[,] { { cell } };

        var water = new WaterBodies
        {
            OceanLevel = 0f,
            MinIngestBboxAreaSquareMeters = minIngestFootprint
        };
        water.AddFromCellData(cell);
        return water;
    }

    private static Slave CreateFloatingSlaveInCurrent(Jitter2.World physicsWorld, ShipModelV1 shipModel,
        float positionX)
    {
        var rigidBody = physicsWorld.CreateRigidBody();
        rigidBody.Position = new JVector(positionX, 10f, 0f);
        rigidBody.Velocity = new JVector(0f, 0f, 3f);

        return new Slave
        {
            RigidBody = rigidBody,
            ShipController = new ShipController(physicsWorld, shipModel),
            CachedWaterFlow = new Vector3(0f, 3f, 0f),
            CachedWaterSurface = 10f,
            CachedFloorLevel = 0f,
            TurnSpeedVelocityMul = 1f,
            Speed = 0f
        };
    }

    private static Slave CreateSlaveWithBuiltHull(Jitter2.World physicsWorld, float massCenterZ,
        float bodyHeight)
    {
        var shipModel = new ShipModelV1
        {
            Mass = 100f,
            MassBoxSizeX = 2f,
            MassBoxSizeY = 6f,
            MassBoxSizeZ = 2f,
            MassCenterZ = massCenterZ
        };
        var controller = new ShipController(physicsWorld, shipModel);
        controller.Build(new JVector(0f, bodyHeight, 0f), new JQuaternion(0f, 0f, 0f, 1f));

        return new Slave
        {
            RigidBody = controller.Hull,
            ShipController = controller,
            CachedWaterFlow = new Vector3(0f, 3f, 0f),
            CachedWaterSurface = 10f,
            CachedFloorLevel = 0f
        };
    }

    private static List<Vector3> CreateRiverContour()
    {
        // First half follows one bank downstream; second half returns along the opposite bank.
        return
        [
            new(0f, 0f, 10f),
            new(10f, 0f, 10f),
            new(20f, 5f, 10f),
            new(20f, 15f, 10f),
            new(10f, 10f, 10f),
            new(0f, 10f, 10f)
        ];
    }

    private static ObjectDataType11Water CreateWaterObject(WaterObjectVolumeType volumeType, float speed,
        List<Vector3> contour, Vector3? fogPlaneNormal = null, float fogPlaneD = -10f)
    {
        const int HeaderSize = 0x7B;
        List<Vector3> shape =
        [
            new(0f, 0f, 10f),
            new(0f, 15f, 10f),
            new(20f, 0f, 10f),
            new(20f, 15f, 10f)
        ];
        var bytes = new byte[HeaderSize + (shape.Count + contour.Count) * 12];

        WriteInt32(bytes, 0x00, (int)ObjectDataType.WaterVolume);
        WriteVector3(bytes, 0x04, new Vector3(0f, 0f, 5f));
        WriteVector3(bytes, 0x10, new Vector3(20f, 15f, 10f));
        bytes[0x2B] = (byte)volumeType;
        bytes[0x2C] = 0x04; // adjacent serialized flags must not become part of the enum value
        WriteVector3(bytes, 0x4B, fogPlaneNormal ?? Vector3.UnitZ);
        WriteSingle(bytes, 0x57, fogPlaneD);
        WriteInt32(bytes, 0x6B, shape.Count);
        WriteSingle(bytes, 0x6F, 5f);
        WriteSingle(bytes, 0x73, speed);
        WriteInt32(bytes, 0x77, contour.Count);

        var offset = HeaderSize;
        foreach (var point in shape)
        {
            WriteVector3(bytes, offset, point);
            offset += 12;
        }
        foreach (var point in contour)
        {
            WriteVector3(bytes, offset, point);
            offset += 12;
        }

        var result = new ObjectDataType11Water();
        var consumed = result.ReadData(bytes, 0);
        if (consumed != bytes.Length)
            throw new InvalidOperationException($"Synthetic water object consumed {consumed} of {bytes.Length} bytes.");
        return result;
    }

    private static void WriteInt32(byte[] target, int offset, int value)
    {
        BitConverter.GetBytes(value).CopyTo(target, offset);
    }

    private static void WriteSingle(byte[] target, int offset, float value)
    {
        BitConverter.GetBytes(value).CopyTo(target, offset);
    }

    private static void WriteVector3(byte[] target, int offset, Vector3 value)
    {
        WriteSingle(target, offset, value.X);
        WriteSingle(target, offset + 4, value.Y);
        WriteSingle(target, offset + 8, value.Z);
    }
}
