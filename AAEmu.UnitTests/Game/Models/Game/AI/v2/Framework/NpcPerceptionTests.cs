using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Framework;

[NotInParallel]
public sealed class NpcPerceptionTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private WorldInstance _world;
    private Region _region;
    private QuietNpc _npc;
    private ProbeBehavior _behavior;
    private uint _nextId;

    [Before(Test)]
    public void SetUp()
    {
        _nextId = 1;
        var worlds = new WorldManager(null, null, null, null, null);
        ReplaceSingleton(worlds);
        ReplaceSingleton(new DuelManager());
        ReplaceSingleton(new QuestManager(null, null));
        ReplaceSingleton(new NpcGameData());
        ReplaceSingleton(new UnitAttributeLimitsGameData());
        var skills = new SkillManager(null, null);
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        ReplaceSingleton(skills);
        var zones = new ZoneManager(null, null);
        SetField(zones, "_zones", new Dictionary<uint, Zone>());
        ReplaceSingleton(zones);
        var factions = new FactionManager(null);
        SetField(factions, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>
        {
            [FactionsEnum.Neutral] = new() { Id = FactionsEnum.Neutral }
        });
        ReplaceSingleton(factions);

        var formulas = new FormulaManager();
        ReplaceSingleton(formulas);
        var engine = new Jace.CalculationEngine();
        engine.AddFunction("clamp", (value, minimum, maximum) => Math.Clamp(value, minimum, maximum));
        typeof(FormulaManager).GetProperty("CalculationEngine")!.SetValue(formulas, engine);
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>
        {
            [5] = new("clamp(1 + (npc_level - pc_level) * 0.03, 0.5, 1) * range") { Id = 5 }
        });

        _world = new WorldInstance(new WorldTemplate
        {
            Id = 1, CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16]
        }, 0, true, 1);
        var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worlds)!;
        instances[_world.Id] = _world;
        _region = new Region(_world, 0, 0, 0);
        SetField(_region, "_neighbors", new[] { _region });

        _npc = Register(new QuietNpc
        {
            Level = 20,
            Template = new NpcTemplate
            {
                Aggression = true, AttackStartRangeScale = 1, SightRangeScale = 1, SightFovScale = 1, Scale = 1
            }
        }, 0);
        _npc.Ai = new ProbeAi { Owner = _npc };
        _behavior = new ProbeBehavior { Ai = _npc.Ai };
    }

    [After(Test)]
    public void TearDown()
    {
        if (_world != null)
            GC.SuppressFinalize(_world);
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HigherLevelCharacter_OutsideReducedFrontRangeDoesNotStartCombatOrAlert(bool alert)
    {
        Character(40, alert ? 14 : 9);

        await Assert.That(Check(alert)).IsFalse();
        await Assert.That(_npc.AggroTable).IsEmpty();
        await Assert.That(_npc.CurrentTarget).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EqualLevelCharacter_InsideFrontRangeStartsCombatOrAlert(bool alert)
    {
        var target = Character(20, alert ? 14.99f : 9.99f);

        await Assert.That(Check(alert)).IsTrue();
        await AssertAcquired(target, alert);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EqualLevelCharacter_AtExistingSearchBoundaryRemainsExcluded(bool alert)
    {
        Character(20, alert ? 15 : 10);

        await Assert.That(Check(alert)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HigherLevelCharacter_AtReducedFrontRangeStartsCombatOrAlert(bool alert)
    {
        var target = Character(30, alert ? 10.5f : 7f);

        await Assert.That(Check(alert)).IsTrue();
        await AssertAcquired(target, alert);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NearerIneligibleCharacter_DoesNotHideFartherEligibleCharacter(bool alert)
    {
        var nearer = Character(100, alert ? 9 : 6);
        var farther = Character(20, alert ? 12 : 8);

        await Assert.That(Check(alert)).IsTrue();
        await AssertAcquired(farther, alert);
        await Assert.That(_npc.AggroTable.ContainsKey(nearer.ObjId)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HigherLevelNonCharacter_KeepsTheOriginalFrontRange(bool alert)
    {
        var target = Register(new QuietNpc { Level = 100, Template = new NpcTemplate() }, alert ? 14 : 9);

        await Assert.That(Check(alert)).IsTrue();
        await AssertAcquired(target, alert);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task HigherLevelCharacter_UsesReducedRearRange(bool alert, bool inside)
    {
        var range = alert ? 1.4f : 1.05f;
        var target = Character(30, -(range + (inside ? -0.05f : 0.05f)));

        await Assert.That(Check(alert)).IsEqualTo(inside);
        if (inside)
            await AssertAcquired(target, alert);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HigherLevelNonCharacter_KeepsTheOriginalRearRange(bool alert)
    {
        var target = Register(new QuietNpc { Level = 100, Template = new NpcTemplate() }, alert ? -1.9f : -1.4f);

        await Assert.That(Check(alert)).IsTrue();
        await AssertAcquired(target, alert);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CharacterBeyondHeightGate_DoesNotStartCombatOrAlert(bool alert)
    {
        var target = Character(20, 1);
        target.Transform.Local.SetPosition(100, 101, 4);

        await Assert.That(Check(alert)).IsFalse();
    }

    [Test]
    [Arguments(false, 0.25f)]
    [Arguments(false, -0.25f)]
    [Arguments(true, 0.25f)]
    [Arguments(true, -0.25f)]
    public async Task InvisibleCharacter_AtPointBlankRangeRemainsUndetected(bool alert, float distance)
    {
        var target = Character(20, distance);
        target.IsVisible = false;

        await Assert.That(Check(alert)).IsFalse();
    }

    private bool Check(bool alert) => alert ? _behavior.CheckAlert() : _behavior.CheckAggression();

    private async Task AssertAcquired(Unit target, bool alert)
    {
        var ai = (ProbeAi)_npc.Ai;
        if (alert)
        {
            await Assert.That(_npc.CurrentTarget).IsSameReferenceAs(target);
            await Assert.That(ai.AlertCount).IsEqualTo(1);
            await Assert.That(_npc.AggroTable).IsEmpty();
        }
        else
        {
            await Assert.That(_npc.AggroTable[target.ObjId].TotalAggro).IsEqualTo(1);
            await Assert.That(ai.CombatCount).IsEqualTo(1);
        }
    }

    private QuietCharacter Character(byte level, float distance) => Register(new QuietCharacter
    {
        Id = _nextId, Level = level
    }, distance);

    private T Register<T>(T unit, float distance) where T : Unit
    {
        unit.ObjId = _nextId++;
        unit.ParentWorld = _world;
        unit.Hp = 100;
        unit.IsVisible = true;
        unit.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        unit.Transform.Local.SetPosition(100, 100 + distance, 0);
        unit.Region = _region;
        _world.AddObject(unit);
        _region.AddObject(unit);
        return unit;
    }

    private void ReplaceSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private static void SetField<T>(T value, string field, object setting) =>
        typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, setting);

    private sealed class QuietNpc : Npc
    {
        public override int MaxHp { get; set; } = 100;
        public override float ModelSize => 2;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class QuietCharacter() : Character(null)
    {
        public override int MaxHp { get; set; } = 100;
    }

    private sealed class ProbeAi : NpcAi
    {
        public int AlertCount { get; private set; }
        public int CombatCount { get; private set; }
        public override void GoToAlert() => AlertCount++;
        public override void GoToCombat() => CombatCount++;
        protected override void Build() { }
    }

    private sealed class ProbeBehavior : Behavior
    {
        public override void Enter() { }
        public override void Tick(TimeSpan delta) { }
        public override void Exit() { }
    }
}
