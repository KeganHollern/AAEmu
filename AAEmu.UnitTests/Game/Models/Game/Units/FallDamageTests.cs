using System.Numerics;
using System.Reflection;
using System.Collections.Concurrent;
using System.Xml;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.IO;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class FallDamageTests
{
    private ClientSource _stockSource;
    private WorldConfig _previousConfig;
    private FieldInfo _managerField;
    private object _previousManager;
    private FieldInfo _worldManagerField;
    private object _previousWorldManager;
    private readonly ConcurrentDictionary<uint, WorldInstance> _worlds = [];

    [Before(Test)]
    public void SetUp()
    {
        var manager = new SkillManager(null, null);
        typeof(SkillManager).GetField("_buffs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<uint, BuffTemplate>());
        _managerField = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousManager = _managerField.GetValue(null);
        _managerField.SetValue(null, manager);
        var worlds = new WorldManager(null, null, null, null, null);
        typeof(WorldManager).GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worlds, _worlds);
        _worldManagerField = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousWorldManager = _worldManagerField.GetValue(null);
        _worldManagerField.SetValue(null, worlds);
    }

    [After(Test)]
    public void TearDown()
    {
        if (_stockSource != null)
        {
            AppConfiguration.Instance.World = _previousConfig;
            ((IList<ClientSource>)ClientFileManager.Sources).Remove(_stockSource);
            _stockSource.Close();
        }
        _managerField.SetValue(null, _previousManager);
        _worldManagerField.SetValue(null, _previousWorldManager);
    }

    [Test]
    [Arguments((ushort)0)]
    [Arguments((ushort)1)]
    [Arguments((ushort)8600)]
    public async Task SafeImpact_DoesNotHealHurtOrSendDamage(ushort impact)
    {
        var unit = new FallUnit { Hp = 500, MaxHp = 1000 };
        await Assert.That(unit.DoFallDamage(impact)).IsEqualTo(0);
        await Assert.That(unit.Hp).IsEqualTo(500);
        await Assert.That(unit.Packets).IsEqualTo(0);
    }

    [Test]
    public async Task OrdinaryFall_PreservesCurveAndUsesFallReason()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        await Assert.That(unit.DoFallDamage(16100)).IsEqualTo(500);
        await Assert.That(unit.Hp).IsEqualTo(500);
        await Assert.That(unit.Reason).IsEqualTo(KillReason.Fall);
        await Assert.That(unit.Packets).IsEqualTo(1);
    }

    [Test]
    [Arguments(1000, 1000, 950)]
    [Arguments(1000, 20, 0)]
    [Arguments(10, 10, 9)]
    public async Task NonlethalFall_PreservesFivePercentFloorWithoutDivisionByZero(int maxHp, int hp, int damage)
    {
        var unit = new FallUnit { Hp = hp, MaxHp = maxHp };
        await Assert.That(unit.DoFallDamage(30000)).IsEqualTo(damage);
        await Assert.That(unit.Hp).IsEqualTo(hp - damage);
    }

    [Test]
    public async Task LethalImpact_UsesFallReasonAndReportsActualDamage()
    {
        var unit = new FallUnit { Hp = 70, MaxHp = 1000 };
        await Assert.That(unit.DoFallDamage(32000)).IsEqualTo(70);
        await Assert.That(unit.Hp).IsEqualTo(0);
        await Assert.That(unit.Reason).IsEqualTo(KillReason.Fall);
    }

    [Test]
    public async Task ActiveFallImmuneBuff_BlocksBothDamageAndStun()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        AddBuff(unit, true);
        await Assert.That(unit.DoFallDamage(ushort.MaxValue)).IsEqualTo(0);
        await Assert.That(unit.Hp).IsEqualTo(1000);
        await Assert.That(unit.Packets).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, EffectState.Acting)]
    [Arguments(true, EffectState.Finishing)]
    [Arguments(true, EffectState.Finished)]
    public async Task InactiveOrEndedBuff_DoesNotGrantFallImmunity(bool inUse, EffectState state)
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        var buff = AddBuff(unit, true);
        buff.InUse = inUse;
        buff.State = state;
        await Assert.That(unit.DoFallDamage(16100)).IsEqualTo(500);
    }

    [Test]
    public async Task WaterImpact_DoesNotDamageOrStun()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        unit.ParentWorld = CreateWorld();
        unit.ParentWorld.Water.OceanLevel = 100;
        unit.Transform.Local.SetPosition(10, 10, 99);
        await Assert.That(unit.DoFallDamage(ushort.MaxValue)).IsEqualTo(0);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task GliderApplyAndRemoveBetweenMoves_DiscardsEarlierFall()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        var buff = AddBuff(unit, true);
        buff.InUse = false;
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task PositionLockAndUnlock_DiscardsEarlierFall()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        unit.DisabledSetPosition = true;
        unit.Transform.Local.SetPosition(1, 1, -100);
        unit.DisabledSetPosition = false;
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task WorldChange_DiscardsEarlierFall()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        unit.ParentWorld = CreateWorld();
        unit.ParentWorld.Water.OceanLevel = -1000;
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task AttachmentAndDetachment_DiscardsEarlierFall()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        unit.Transform.Parent = new Unit().Transform;
        unit.Transform.Parent = null;
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task RepeatedLandingEvent_DoesNotApplyDamageTwice()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        var hp = unit.Hp;
        await Assert.That(hp).IsLessThan(1000);
        unit.ObserveFallMovement(true, 1.1);
        await Assert.That(unit.Hp).IsEqualTo(hp);
    }

    [Test]
    public async Task OmittedReport_OnLoadedTerrainStillAppliesImpact()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000, ParentWorld = CreateWorld(true) };
        SeedFall(unit);
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(false, 1);
        await Assert.That(unit.Hp).IsLessThan(1000);
    }

    [Test]
    public async Task AirborneStationaryPacket_OnLoadedTerrainDoesNotDamageOrReset()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000, ParentWorld = CreateWorld(true) };
        SeedFall(unit);
        unit.ObserveFallMovement(false, 0.6);
        await Assert.That(unit.Hp).IsEqualTo(1000);
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(false, 1.1);
        await Assert.That(unit.Hp).IsLessThan(1000);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StockDryHole_ShortJumpUsesOneLandingReport(bool delayed)
    {
        var world = CreateStockWorld();
        const float x = 22416.2f, y = 10020.2f, rock = 392.323f;
        await Assert.That(world.GetHeight(x, y)).IsGreaterThan(rock + 1.3f);
        var actor = new FallUnit { Hp = 4026, MaxHp = 4026, ParentWorld = world };
        void Move(float z, double time, bool landing = false)
        {
            actor.Transform.Local.SetPosition(x, y, z);
            actor.ObserveFallMovement(landing, time);
        }
        Move(rock, 0);
        Move(rock + 0.975f, 0.16);
        Move(rock + 1.3f, 0.32);
        Move(rock + 0.975f, 0.48);
        Move(rock, 0.64, !delayed);
        if (delayed)
        {
            await Assert.That(actor.Impacts).IsEmpty();
            Move(rock, 0.74, true);
        }
        Move(rock, 0.75, true);
        await Assert.That(actor.Impacts).IsEquivalentTo(new ushort[] { 3120 });
        await Assert.That(actor.Hp).IsEqualTo(4026);
    }

    [Test]
    [Arguments(12470f, 16104f, 264.938507f, 264.89465f)]
    [Arguments(12656.44f, 14530.777f, 170.02567f, 169.9737f)]
    public async Task StockSolidTerrain_ShortJumpLandsBeforeDelayedReport(float x, float y, float foot, float legacy)
    {
        var world = CreateStockWorld();
        await Assert.That(world.GetHeight(x, y)).IsEqualTo(legacy).Within(0.0001f);
        await Assert.That(world.Template.TryGetFallTerrainHeight(x, y, out var height)).IsTrue();
        await Assert.That(height).IsEqualTo(foot).Within(FallMovement.PositionTolerance);

        var actor = new FallUnit { Hp = 4026, MaxHp = 4026, ParentWorld = world };
        void Move(float offset, double time, bool reported = false)
        {
            actor.Transform.Local.SetPosition(x, y, foot + offset);
            actor.ObserveFallMovement(reported, time);
        }
        Move(0, 0);
        Move(0.975f, 0.16);
        Move(1.3f, 0.32);
        Move(0.975f, 0.48);
        await Assert.That(actor.Impacts).IsEmpty();
        Move(0, 0.64);
        await Assert.That(actor.Impacts).IsEquivalentTo(new ushort[] { 3120 });
        Move(0, 0.74, true);
        Move(0, 0.75, true);
        Move(0, 6);
        await Assert.That(actor.Impacts).IsEquivalentTo(new ushort[] { 3120 });
        await Assert.That(actor.Hp).IsEqualTo(4026);
    }

    [Test]
    public async Task StockSolidTerrain_FallUsesTheTriangleInsideTheCell()
    {
        var world = CreateStockWorld();
        const float x = 12512.375f, y = 16382.625f;
        await Assert.That(world.Template.TryGetFallTerrainHeight(x, y, out var height)).IsTrue();
        await Assert.That(height).IsEqualTo(477.85873f).Within(0.0001f);
        await Assert.That(world.Template.TryGetFallTerrainHeight(12512, 16384, out var shared)).IsTrue();
        await Assert.That(shared).IsEqualTo(478.6059f).Within(0.0001f);
        // The north cell edge intersects a sector boundary here. The two signed sectors
        // contain distinct endpoint heights; the owning triangle uses the first authored vertex.
        await Assert.That(world.Template.TryGetFallTerrainHeight(12672.375f, 16382.625f, out var junction)).IsTrue();
        await Assert.That(junction).IsEqualTo(513.61926f).Within(0.0001f);
    }

    [Test]
    public async Task StockTerrain_FallHeightUsesTheSquareOnEachSideOfABoundary()
    {
        var world = CreateStockWorld();
        // Adjacent surface-31 and solid squares across sector X, cell X, and the corner of four cells.
        foreach (var (x, y, solid) in new[]
        {
            (5823.875f, 12756.125f, false), (5824f, 12756.125f, true),
            (7167.875f, 11482.125f, false), (7168f, 11482.125f, true),
            (6143.875f, 8192.125f, true), (6144.125f, 8191.875f, true), (6144f, 8192f, false)
        })
        {
            await Assert.That(world.TryGetHeight(x, y, out _)).IsTrue();
            await Assert.That(world.Template.TryGetFallTerrainHeight(x, y, out var height)).IsEqualTo(solid);
            if (solid)
            {
                var cx = (int)x / WorldManager.CELL_SIZE;
                var cy = (int)y / WorldManager.CELL_SIZE;
                using var input = _stockSource.GetFileStream($"game/worlds/main_world/cells/{cx:000}_{cy:000}/client/terrain/heightmap.dat");
                var native = CryTerrainGrid.Read(input, new Vector2(cx, cy) * WorldManager.CELL_SIZE);
                await Assert.That(height).IsEqualTo(native.SampleHeight(x, y)).Within(0.0001f);
            }
        }
    }

    private WorldInstance CreateStockWorld()
    {
        var archive = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(archive), "Set AAEMU_HOUSING_GAME_PAK to check the stock terrain.");
        if (!ClientFileManager.AddSource(archive))
            throw new IOException("Could not open the stock terrain archive.");
        _stockSource = ClientFileManager.Sources[^1];
        _previousConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new() { GeoDataMode = false };
        var template = new WorldTemplate { Name = "main_world" };
        using var input = _stockSource.GetFileStream("game/worlds/main_world/world.xml");
        var xml = new XmlDocument();
        xml.Load(input);
        template.XmlWorld.ReadNode(xml.DocumentElement, template);
        var world = new WorldInstance(template, 0, true, 0);
        world.Water.OceanLevel = 100;
        _worlds[0] = world;
        return world;
    }

    [Test]
    public async Task DeadMovement_DiscardsHistoryBeforeRevival()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        unit.Hp = 0;
        unit.ObserveFallMovement(true, 0.6);
        unit.Hp = 1000;
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task StickyAttachmentAndDetachment_DiscardsEarlierFall()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        unit.Transform.StickyParent = new Unit().Transform;
        unit.Transform.StickyParent = null;
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task DeathCallback_DiscardsEarlierFallBeforeAnyNewMovement()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000, ParentWorld = CreateWorld() };
        SeedFall(unit);
        unit.Hp = 0;
        unit.DoDie(unit, KillReason.Fall);
        unit.Hp = 1000;
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
    }

    [Test]
    public async Task GlidingWithoutAuthoredImmunity_DiscardsDescentUntilGliderEnds()
    {
        var unit = new FallUnit { Hp = 1000, MaxHp = 1000 };
        SeedFall(unit);
        var glider = AddBuff(unit, false, true);
        unit.Transform.Local.SetPosition(1, 1, -100);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsEqualTo(1000);
        glider.InUse = false;
        SeedFall(unit);
        unit.Transform.Local.SetPosition(1, 1, 0);
        unit.ObserveFallMovement(true, 1);
        await Assert.That(unit.Hp).IsLessThan(1000);
    }

    private WorldInstance CreateWorld(bool terrain = false)
    {
        var template = new WorldTemplate { Id = 1, Name = "fall_test", HeightMaxCoefficient = 1 };
        if (terrain)
        {
            var cell = new WorldCell(0, 0, template);
            typeof(WorldCell).GetProperty(nameof(WorldCell.HeightMap), BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cell,
                new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION]);
            typeof(WorldCell).GetProperty(nameof(WorldCell.Loaded))!.SetValue(cell, true);
            template.Cells[0, 0] = cell;
        }
        var world = new WorldInstance(template, 0, true, 0);
        world.Water.OceanLevel = -1000;
        _worlds[0] = world;
        return world;
    }

    private static void SeedFall(FallUnit unit)
    {
        unit.Transform.Local.SetPosition(1, 1, 20);
        unit.ObserveFallMovement(false, 0);
        unit.Transform.Local.SetPosition(1, 1, 10);
        unit.ObserveFallMovement(false, 0.5);
    }

    private static Buff AddBuff(Unit unit, bool immune, bool gliding = false)
    {
        var buff = new Buff(unit, unit, new SkillCasterUnit(unit.ObjId),
            new BuffTemplate { Id = 1, FallDamageImmune = immune, Gliding = gliding }, null, DateTime.UtcNow)
        { InUse = true, State = EffectState.Acting };
        var effects = (List<Buff>)typeof(Buffs).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(unit.Buffs)!;
        effects.Add(buff);
        return buff;
    }

    private sealed class FallUnit : Unit
    {
        public List<ushort> Impacts { get; } = [];
        public override int DoFallDamage(ushort fallVel)
        {
            Impacts.Add(fallVel);
            return base.DoFallDamage(fallVel);
        }

        public KillReason Reason { get; private set; }
        public int Packets { get; private set; }
        public override void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage)
        {
            Reason = killReason;
            Hp = Math.Max(0, Hp - value);
        }
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets++;
    }
}
