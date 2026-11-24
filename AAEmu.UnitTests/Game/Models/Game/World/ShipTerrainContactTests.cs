using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Physics;

using Jitter2.LinearMath;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public sealed class ShipTerrainContactTests
{
    private const int Edge = WorldManager.CELL_SIZE;
    private const int HeightUnitsPerMeter = 8;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(20);

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    public async Task Cliff_FlatAndSteepBanksUseMeasuredSamples(bool otherCell, bool steep)
    {
        using var physics = new Jitter2.World();
        // The Clipper faces the known cliff along Y. The missing cell is on its starboard side along X.
        var ship = CreateShip(physics, steep ? 100 : 20,
            (_, y) => steep ? 98 + (y - 20) * 2 : 21, otherCell);
        if (!steep)
            ship.RigidBody.Orientation = JQuaternion.CreateRotationY(MathF.PI / 2);
        var before = ship.RigidBody.Position;
        new ShipCliffInteraction().ResolveAll(ship.ParentWorld, [ship], Step);

        await Assert.That(ship.StaticObstacleHullDamageContactActive).IsEqualTo(steep);
        await Assert.That(ship.RigidBody.Position.Equals(before)).IsEqualTo(!steep);
    }

    [Test]
    [Arguments(2f, 20f, false)]
    [Arguments(20f, 2f, true)]
    [Arguments(20f, Edge - 3f, true)]
    public async Task Cliff_UsesMeasuredSlopeOnEitherSideOfBothAxes(float x, float y, bool bankRisesAlongX)
    {
        using var physics = new Jitter2.World();
        var ship = CreateShip(physics, 100, (sampleX, sampleY) =>
            98 + ((bankRisesAlongX ? sampleX : sampleY) - 20) * 2, otherCell: false);
        ship.RigidBody.Position = new JVector(x, 99.5f, y);
        if (bankRisesAlongX)
            ship.RigidBody.Orientation = JQuaternion.CreateRotationY(MathF.PI / 2);
        new ShipCliffInteraction().ResolveAll(ship.ParentWorld, [ship], Step);
        await Assert.That(ship.StaticObstacleHullDamageContactActive).IsTrue();
    }

    [Test]
    public async Task Cliff_CannotInferSlopeWithUnmeasuredCenterAndMissingSide()
    {
        using var physics = new Jitter2.World();
        var ship = CreateShip(physics, 20, (_, _) => 21, otherCell: false);
        ship.RigidBody.Orientation = JQuaternion.CreateRotationY(MathF.PI / 2);
        ship.RigidBody.Position = new JVector(Edge + 1, 19.5f, 20);
        new ShipCliffInteraction().ResolveAll(ship.ParentWorld, [ship], Step);
        await Assert.That(ship.StaticObstacleHullDamageContactActive).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(70)]
    public async Task DeepOcean_KnownBedDoesNotGroundShip(int bed)
    {
        using var physics = new Jitter2.World();
        var ship = CreateShip(physics, 100, (_, _) => bed, otherCell: true);
        new ShipCliffInteraction().ResolveAll(ship.ParentWorld, [ship], Step);
        new ShipShoreInteraction().ResolveTerrainContacts(ship, Step, physics);
        await Assert.That(ship.StaticObstacleHullDamageContactActive).IsFalse();
        await Assert.That(ship.GroundContactLatched).IsFalse();
        await Assert.That(ship.GroundContactFloorSmoothingSeeded).IsTrue();
        await Assert.That(ship.GroundContactFloorSmoothed).IsEqualTo((float)bed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Shore_FirstAvailableProbeAfterGapStartsFromMeasuredBank(bool measuredZero)
    {
        using var physics = new Jitter2.World();
        var ship = CreateShip(physics, 100, (x, _) => x >= Edge ? 0 : 102, measuredZero);
        var contacts = new ShipShoreInteraction();
        ship.RigidBody.Position = new JVector(Edge + 6, 99.5f, 20);
        contacts.ResolveTerrainContacts(ship, Step, physics);
        await Assert.That(ship.GroundContactLatched).IsFalse();
        await Assert.That(ship.GroundContactFloorSmoothingSeeded).IsEqualTo(measuredZero);

        ship.RigidBody.Position = new JVector(Edge - 6, 99.5f, 20);
        contacts.ResolveTerrainContacts(ship, Step, physics);
        await Assert.That(ship.GroundContactLatched).IsEqualTo(!measuredZero);
        if (!measuredZero)
            await Assert.That(ship.GroundContactFloorSmoothed).IsEqualTo(102f);
    }

    [Test]
    public async Task Shore_MissingBowSampleEndsUnprovedContactAndItsDamageInterval()
    {
        using var physics = new Jitter2.World();
        var ship = CreateShip(physics, 100, (_, _) => 130, otherCell: false);
        var contacts = new ShipShoreInteraction();
        contacts.ResolveTerrainContacts(ship, Step, physics);
        await Assert.That(ship.GroundContactLatched).IsTrue();
        await Assert.That(ship.ShoreGroundDamageSecondsAccumulator).IsGreaterThan(0f);
        ship.RigidBody.Position = new JVector(Edge + 6, 99.5f, 20);
        contacts.ResolveTerrainContacts(ship, TimeSpan.FromMilliseconds(1), physics);
        await Assert.That(ship.GroundContactLatched).IsFalse();
        await Assert.That(ship.ShoreGroundDamageSecondsAccumulator).IsEqualTo(0f);
        await Assert.That(ship.GroundContactFloorSmoothingSeeded).IsFalse();
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Shore_OnlyMeasuredContactCanCompleteTheDamageInterval(bool missing, bool takesDamage)
    {
        using var physics = new Jitter2.World();
        var ship = CreateShip(physics, 100, (_, _) => 130, otherCell: false);
        var contact = new ShipShoreInteraction();
        for (var tick = 0; tick < 49; tick++)
            contact.ResolveTerrainContacts(ship, Step, physics);
        contact.ResolveTerrainContacts(ship, TimeSpan.FromMilliseconds(19), physics);
        await Assert.That(ship.GroundContactLatched).IsTrue();
        await Assert.That(ship.Hp).IsEqualTo(10000);

        if (missing)
            ship.RigidBody.Position = new JVector(Edge + 6, 99.5f, 20);
        contact.ResolveTerrainContacts(ship, TimeSpan.FromMilliseconds(2), physics);
        await Assert.That(ship.Hp).IsEqualTo(takesDamage ? 9900 : 10000);
        await Assert.That(ship.Packets.OfType<SCEnvDamagePacket>().Count()).IsEqualTo(takesDamage ? 1 : 0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Shore_VisiblePitchUsesTwoMeasuredEnds(bool otherCell, bool incline)
    {
        using var physics = new Jitter2.World();
        var ship = CreateShip(physics, 100, (_, y) => incline ? 92 + y / 2 : 102, otherCell);
        var shore = new ShipShoreInteraction();
        shore.ResolveTerrainContacts(ship, Step, physics);
        await Assert.That(ship.GroundContactLatched).IsTrue();
        if (!incline)
            ship.RigidBody.Orientation = JQuaternion.CreateRotationY(MathF.PI / 2);
        shore.UpdateVisualGroundPitch(ship, ship.RigidBody, Step);
        await Assert.That(ship.GroundPitchFloorSmoothingSeeded).IsEqualTo(otherCell);
        await Assert.That(MathF.Abs(ship.GroundPitchAngle) > 0f).IsEqualTo(incline);
    }

    private sealed class ContactShip : Slave
    {
        public override int MaxHp => 10000;
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }

    private static ContactShip CreateShip(Jitter2.World physics, float water, Func<int, int, int> bed, bool otherCell)
    {
        var template = WorldTemplateTests.CreateWorldTemplate(2, 1);
        template.HeightMaxCoefficient = HeightUnitsPerMeter;
        for (var cellX = 0; cellX < (otherCell ? 2 : 1); cellX++)
        {
            var map = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
            for (var x = 0; x < map.GetLength(0); x++)
                for (var y = 0; y < map.GetLength(1); y++)
                    map[x, y] = (ushort)Math.Clamp(bed(cellX * Edge + x * 2, y * 2) * HeightUnitsPerMeter, 0, ushort.MaxValue);
            WorldTemplateTests.SetLoadedCell(template, cellX, 0, map);
        }

        var world = new WorldInstance(template, 0, true, 0) { Water = new WaterBodies { OceanLevel = water } };
        var controller = new ShipController(physics, new ShipModelV1
        {
            Mass = 5000, MassBoxSizeX = 4, MassBoxSizeY = 8, MassBoxSizeZ = 7, MassCenterZ = 3, KeelHeight = 0.1f
        });
        controller.Build(new JVector(Edge - 3, water - 0.5f, 20), JQuaternion.Identity);
        var ship = new ContactShip { Hp = 10000, ShipController = controller, RigidBody = controller.Hull, Region = new Region(world, 15, 0, 0) };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ship, world);
        ship.Transform.Local.SetPosition(Edge - 3, 20, water - 0.5f);
        ship.CreateWaterAndLandSurfaceCache();
        return ship;
    }
}
