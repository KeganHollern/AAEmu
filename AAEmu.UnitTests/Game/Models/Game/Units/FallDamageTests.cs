using System.Reflection;
using System.Collections.Concurrent;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class FallDamageTests
{
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
