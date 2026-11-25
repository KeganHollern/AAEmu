using System.Numerics;
using System.Reflection;
using System.Xml;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.IO;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Physics;
using Jitter2.LinearMath;

namespace AAEmu.UnitTests.Game.Models.Game.World;

[NotInParallel]
public sealed class DewstoneClipperApproachTests
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<(FieldInfo Field, object Value)> _previous = [];

    private sealed class Clipper : Slave
    {
        public override int MaxHp => 9216;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    [After(Test)]
    public void RestoreModels()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments(0, 0f, true)]
    [Arguments(0, 0.25f, true)]
    [Arguments(0, -1f, true)]
    [Arguments(10, 10.25f, true)]
    [Arguments(10, 14f, false)]
    [Arguments(5, 4f, true)]
    public async Task ClipperUsesMeasuredSeabedForShallowHelmControls(int bed, float water, bool capped)
    {
        SetUpModels();
        var template = WorldTemplateTests.CreateWorldTemplate(2, 1);
        WorldTemplateTests.SetLoadedCell(template, 0, 0, WorldTemplateTests.CreateHeightMap((ushort)bed));
        var ship = new Clipper
        {
            ModelId = 128,
            Template = new SlaveTemplate { Id = 14, ModelId = 128, SlaveKind = (SlaveKind)3 }
        };
        var world = new WorldInstance(template, 0, true, ship.Transform.InstanceId)
        {
            Water = new WaterBodies { OceanLevel = water },
            Regions = new Region[2 * WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        };
        ship.ParentWorld = world;
        using var physics = new Jitter2.World();
        var controller = new ShipController(physics, ModelManager.Instance.GetShipModel(128));
        controller.Build(new JVector(12, water, 12), JQuaternion.Identity);
        ship.ShipController = controller;
        ship.RigidBody = controller.Hull;

        async Task CheckHelmAt(float x, bool expectGroundControls)
        {
            ship.Transform.Local.SetPosition(x, 12, water);
            ship.RigidBody.Position = new JVector(x, water, 12);
            ship.CreateWaterAndLandSurfaceCache();
            ship.Throttle = ship.ThrottleRequest = 100;
            ship.Speed = 2f;
            controller.ApplyForceAndTorque(ship, TimeSpan.FromMilliseconds(40));
            await Assert.That(ship.GroundedLastTick).IsEqualTo(expectGroundControls);
            await Assert.That(ship.Throttle < 100).IsEqualTo(expectGroundControls);
        }

        const float missingCellX = WorldManager.CELL_SIZE + 12f;
        await CheckHelmAt(missingCellX, false);
        await CheckHelmAt(12, capped);
        await CheckHelmAt(missingCellX, false);
        ship.GroundContactLatched = true;
        await CheckHelmAt(missingCellX, true);
        ship.GroundContactLatched = false;
        await CheckHelmAt(12, capped);
    }

    [Test]
    [Arguments(12534.6f, 14553.3f, -152.7f, 0)]
    [Arguments(12547f, 14428f, -152.7f, 0)]
    [Arguments(12547f, 14428f, -152.7f, 127)]
    [Arguments(12551f, 14422f, 180f, 127)]
    public async Task ClipperReachesLowerWaterFromOrdinaryUpperRiver(float x, float y, float yaw, int throttle)
    {
        var (template, water) = await ReadDewstone();
        SetUpModels();
        var ship = new Clipper
        {
            Hp = 9216, ModelId = 128,
            Template = new SlaveTemplate { Id = 14, ModelId = 128, SlaveKind = (SlaveKind)3 }
        };
        var world = new WorldInstance(template, 0, true, ship.Transform.InstanceId) { Water = water };
        world.Regions = new Region[template.CellX * WorldManager.SECTORS_PER_CELL, template.CellY * WorldManager.SECTORS_PER_CELL];
        world.SlaveManager = new SlaveManager(world);
        ship.ParentWorld = world;
        ship.Transform.Local.SetPosition(x, y, 111.53369f, 0, 0, yaw * MathF.PI / 180f);
        ship.Region = new Region(world, (int)(x / WorldManager.REGION_SIZE), (int)(y / WorldManager.REGION_SIZE), 0);
        ship.AttachedCharacters[AttachPointKind.Driver] = new Character(null);
        ship.ThrottleRequest = (sbyte)throttle;

        var originalSettings = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World ??= new();
        var manager = new PhysicsManager { SimulationWorld = world };
        manager.Initialize();
        manager.AddShip(ship);
        try
        {
            var sync = typeof(PhysicsManager).GetMethod("SyncTransformWithRigidBody", InstanceFields)!
                .CreateDelegate<Action<Slave>>(manager);
            var helm = typeof(PhysicsManager).GetMethod("BoatPhysicsTick", InstanceFields)!
                .CreateDelegate<Action<Slave, TimeSpan>>(manager);
            var falls = new ShipWaterfallInteraction();
            var shore = new ShipShoreInteraction();
            var cliffs = new ShipCliffInteraction();
            var dt = TimeSpan.FromMilliseconds(40);
            var launches = 0;
            var recoveries = 0;
            var landed = false;
            var launchSpeed = 0f;
            List<WaterfallArea> launchBrushes = [];
            var lastMeasurement = Vector2.Zero;
            var lastTick = -2;
            // Step the same body with buoyancy, river flow, helm and terrain contacts.
            // After spawn the only input is the ordinary throttle request.
            for (var tick = 0; tick < 25 * 125 && !landed; tick++)
            {
                manager._physWorld.Step((float)dt.TotalSeconds, false);
                var published = ship.Transform.World.Position;
                var xy = new Vector2(published.X, published.Y);
                if (tick - lastTick >= 2 || Vector2.DistanceSquared(xy, lastMeasurement) > 1f)
                {
                    ship.CreateWaterAndLandSurfaceCache();
                    lastMeasurement = xy;
                    lastTick = tick;
                }
                sync(ship);
                var wasFlying = ship.WaterfallTransitActive;
                if (!wasFlying)
                    helm(ship, dt);
                var before = ship.RigidBody.Position;
                var flying = falls.Update(ship, dt);
                var after = ship.RigidBody.Position;
                if (after.Y > 105f && (before - after).LengthSquared() > 0.01f)
                    recoveries++;
                if (flying && !wasFlying)
                {
                    launches++;
                    var box = ship.RigidBody.Shapes[0].WorldBoundingBox;
                    launchBrushes = water.GetWaterfallsIntersecting(box.Min.X - 3f, box.Min.Z - 3f, box.Min.Y - 2.5f,
                        box.Max.X + 3f, box.Max.Z + 3f, box.Max.Y + 2.5f);
                    var v = ship.RigidBody.Velocity;
                    launchSpeed = new Vector2(v.X, v.Z).Length();
                }
                if (flying)
                    continue;
                landed = wasFlying && ship.CachedWaterSurface == 100f && after.Y < 101f;
                shore.ResolveTerrainContacts(ship, dt, manager._physWorld);
                ship.ShipController.ApplyForceAndTorque(ship, dt);
                cliffs.ResolveAll(world, [ship], dt);
            }
            await Assert.That(recoveries).IsEqualTo(0);
            await Assert.That(launches).IsEqualTo(1);
            await Assert.That(landed).IsTrue();
            await Assert.That(launchBrushes.Count).IsEqualTo(2);
            var landedBounds = ship.RigidBody.Shapes[0].WorldBoundingBox;
            foreach (var brush in launchBrushes)
            {
                var cleared = landedBounds.Max.X < brush.Min.X || landedBounds.Min.X > brush.Max.X ||
                              landedBounds.Max.Z < brush.Min.Y || landedBounds.Min.Z > brush.Max.Y;
                await Assert.That(cleared).IsTrue();
            }
            await Assert.That(launchSpeed).IsLessThanOrEqualTo(ShipWaterfallInteraction.MaximumReplicableHorizontalSpeed + 0.00001f);
            await Assert.That(ship.CachedFloorLevel).IsLessThan(99.75f);
            await Assert.That(ship.Hp).IsEqualTo(ship.MaxHp);
        }
        finally
        {
            manager.RemoveShip(ship);
            manager.Dispose();
            AppConfiguration.Instance.World = originalSettings;
        }
    }

    private static async Task<(WorldTemplate Template, WaterBodies Water)> ReadDewstone()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK to test the signed Dewstone river.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        var template = new WorldTemplate();
        var xml = new XmlDocument();
        WaterBodies water;
        try
        {
            using var worldFile = source.GetFileStream("game/worlds/main_world/world.xml");
            xml.Load(worldFile);
            template.XmlWorld.ReadNode(xml.DocumentElement!, template);
            const string cell = "game/worlds/main_world/cells/012_014/client";
            using var objectFile = source.GetFileStream(cell + "/object.dat");
            var objects = new ObjectsFile(cell);
            await Assert.That(objects.ReadFile(objectFile)).IsTrue();
            water = new WaterBodies { OceanLevel = template.OceanLevel };
            water.AddFromCellData(new WorldCell(12, 14, template) { LoadedObjectDat = objects });
            using var heightFile = source.GetFileStream(cell + "/terrain/heightmap.dat");
            var grid = CryTerrainGrid.Read(heightFile, new Vector2(12, 14) * WorldManager.CELL_SIZE);
            var heights = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
            for (var ix = 0; ix < heights.GetLength(0); ix++)
            for (var iy = 0; iy < heights.GetLength(1); iy++)
                heights[ix, iy] = (ushort)(template.HeightMaxCoefficient *
                    grid.SampleRawHeight(12 * WorldManager.CELL_SIZE + 2 * ix, 14 * WorldManager.CELL_SIZE + 2 * iy));
            WorldTemplateTests.SetLoadedCell(template, 12, 14, heights);
        }
        finally
        {
            source.Close();
        }

        return (template, water);
    }

    private void SetUpModels()
    {
        // Stock slave 14 -> model 128 -> ship model 3.
        var model = new ShipModelV1
        {
            Mass = 5000, MassCenterZ = 3, MassBoxSizeX = 4, MassBoxSizeY = 8, MassBoxSizeZ = 7,
            Velocity = 13, WaterDensity = 1, WaterResistance = 1, SteerVel = 5, Accel = 1,
            ReverseAccel = 0.5f, ReverseVelocity = 5, TurnAccel = 1.5f, KeelHeight = 0.1f
        };
        var models = new ModelManager();
        typeof(ModelManager).GetField("_modelTypes", InstanceFields)!.SetValue(models, new Dictionary<uint, ModelType>
            { [128] = new() { Id = 128, SubType = "ShipModel", SubId = 3 } });
        typeof(ModelManager).GetField("_models", InstanceFields)!.SetValue(models, new Dictionary<string, Dictionary<uint, Model>>
            { ["ShipModel"] = new() { [3] = model } });
        Install(models);
        Install(new WorldManager(null, null, null, null, null));
    }

    private void Install<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add((field, field.GetValue(null)!));
        field.SetValue(null, value);
    }
}
