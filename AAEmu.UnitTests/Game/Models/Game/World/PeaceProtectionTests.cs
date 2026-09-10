using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Duels;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Duels;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.World;

[NotInParallel]
public sealed class PeaceProtectionTests
{
    private readonly List<Action> _restore = [];
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
    private ZoneConflict _sourceConflict;
    private ZoneConflict _targetConflict;
    private CharacterMock _attacker;
    private CharacterMock _target;
    private DuelManager _duels;

    [Before(Test)]
    public void SetUp()
    {
        _sourceConflict = CreateConflict();
        _targetConflict = CreateConflict();
        var zones = new ZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<ITaskManager>().Object);
        SetField(zones, "_zones", new Dictionary<uint, Zone>
        {
            [100] = new() { ZoneKey = 100, GroupId = 22, FactionId = FactionsEnum.Neutral },
            [200] = new() { ZoneKey = 200, GroupId = 23, FactionId = FactionsEnum.Neutral }
        });
        SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>
        {
            [22] = new() { Id = 22, Conflict = _sourceConflict },
            [23] = new() { Id = 23, Conflict = _targetConflict }
        });
        InstallSingleton(zones);

        var factions = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        var west = new SystemFaction { Id = FactionsEnum.NuiaAlliance };
        var east = new SystemFaction { Id = FactionsEnum.HaranyaAlliance };
        var pirate = new SystemFaction { Id = FactionsEnum.Pirate };
        foreach (var faction in new[] { west, east, pirate })
            foreach (var other in new[] { west, east, pirate })
                faction.Relations[other.Id] = new() { State = faction == other ? RelationState.Friendly : RelationState.Hostile };
        SetField(factions, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>
        {
            [west.Id] = west,
            [east.Id] = east,
            [pirate.Id] = pirate,
            [FactionsEnum.Neutral] = new() { Id = FactionsEnum.Neutral }
        });
        InstallSingleton(factions);
        InstallSingleton(new TeamManager(Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object,
            Mock.Of<ITeamIdManager>().Object));
        _duels = new DuelManager();
        InstallSingleton(_duels);
        _attacker = CreateCharacter(1, 100, FactionsEnum.NuiaAlliance);
        _target = CreateCharacter(2, 200, FactionsEnum.HaranyaAlliance);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var restore in _restore)
            restore();
    }

    [Test]
    [Arguments(FactionsEnum.NuiaAlliance)]
    [Arguments(FactionsEnum.HaranyaAlliance)]
    [Arguments(FactionsEnum.Pirate)]
    public async Task CanAttack_PeaceEntryAndExit_ChangesEligibilityWithoutChangingRelation(FactionsEnum targetFaction)
    {
        _target.Faction = FactionManager.Instance.GetFaction(targetFaction);
        _attacker.Faction = FactionManager.Instance.GetFaction(targetFaction == FactionsEnum.NuiaAlliance ? FactionsEnum.HaranyaAlliance : FactionsEnum.NuiaAlliance);
        await Assert.That(_attacker.CanAttack(_target)).IsTrue();

        _targetConflict.SetState(ZoneConflictType.Peace);
        await Assert.That(_attacker.CanAttack(_target)).IsFalse();
        await Assert.That(_attacker.GetRelationStateTo(_target)).IsEqualTo(RelationState.Hostile);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _targetConflict.CheckTimer();
        await Assert.That(_attacker.CanAttack(_target)).IsTrue();
    }

    [Test]
    [Arguments(0u, false, false)]
    [Arguments(4u, true, true)]
    [Arguments(5u, true, true)]
    [Arguments(148u, true, false)]
    [Arguments(149u, false, true)]
    public async Task PreventsAttack_AuthoredFactionSelector_ProtectsMatchingFactions(uint selector, bool west, bool east)
    {
        _targetConflict.PeaceProtectedFactionId = selector;
        _targetConflict.SetState(ZoneConflictType.Peace);
        _target.Faction = new() { Id = FactionsEnum.Nuian, MotherId = FactionsEnum.NuiaAlliance };
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsEqualTo(west);
        _target.Faction = new() { Id = FactionsEnum.Harani, MotherId = FactionsEnum.HaranyaAlliance };
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsEqualTo(east);
    }

    [Test]
    public async Task PreventsAttack_ClosedOrMissingConflict_DoesNotProtect()
    {
        _targetConflict.SetState(ZoneConflictType.Peace);
        _targetConflict.Closed = true;
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsFalse();
        SetField(_target.Transform, "_zoneId", 999u);
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CanAttack_ForcedAttack_DoesNotOverridePeace(bool sameFaction)
    {
        if (sameFaction)
            _target.Faction = _attacker.Faction;
        _attacker.ForceAttack = true;
        _targetConflict.SetState(ZoneConflictType.Peace);
        await Assert.That(_attacker.CanAttack(_target)).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task CanAttack_Retribution_OnlyOverridesFriendlyProtection(bool sameFaction, bool expected)
    {
        if (sameFaction)
            _target.Faction = _attacker.Faction;
        SetRetribution(_target);
        _targetConflict.SetState(ZoneConflictType.Peace);
        await Assert.That(_attacker.CanAttack(_target)).IsEqualTo(expected);
    }

    [Test]
    public async Task CanAttack_Retaliation_UsesAttackerTargetPair()
    {
        _targetConflict.SetState(ZoneConflictType.Peace);
        _attacker.IsInBattle = true;
        _attacker.SetHostileActivity(_target);
        await Assert.That(_attacker.CanAttack(_target)).IsTrue();
        var other = CreateCharacter(3, 100, FactionsEnum.NuiaAlliance);
        await Assert.That(other.CanAttack(_target)).IsFalse();
        _attacker.IsInBattle = false;
        await Assert.That(_attacker.CanAttack(_target)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task PreventsAttack_ProtectedLevelTen_BlocksBeforeExceptions(bool source)
    {
        var protectedUnit = source ? _attacker : _target;
        var conflict = source ? _sourceConflict : _targetConflict;
        protectedUnit.Level = 10;
        conflict.SetState(ZoneConflictType.Peace);
        _attacker.IsInBattle = true;
        _attacker.SetHostileActivity(_target);
        SetRetribution(_target);
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsTrue();
        protectedUnit.Level = 11;
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsFalse();
    }

    [Test]
    public async Task PreventsAttack_OwnedUnits_UseTheirOwnZoneAndAttackerObject()
    {
        var mate = new OwnedUnit(_target) { ObjId = 20, Level = 50, Faction = _target.Faction, Hp = 100 };
        mate.Transform.ZoneId = 200;
        SetField(_target.Transform, "_zoneId", 100u);
        _targetConflict.SetState(ZoneConflictType.Peace);
        _attacker.IsInBattle = true;
        await Assert.That(_attacker.CanAttack(mate)).IsFalse();
        _attacker.SetHostileActivity(_target);
        await Assert.That(_attacker.CanAttack(mate)).IsFalse();
        _attacker.SetHostileActivity(mate);
        await Assert.That(_attacker.CanAttack(mate)).IsTrue();

        var attackerMate = new OwnedUnit(_attacker) { ObjId = 10, Level = 50, Faction = _attacker.Faction };
        attackerMate.Transform.ZoneId = 100;
        await Assert.That(PeaceProtection.PreventsAttack(attackerMate, mate)).IsTrue();
    }

    [Test]
    public async Task PreventsAttack_NpcAndSelfDamage_DoNotUsePlayerProtection()
    {
        _sourceConflict.SetState(ZoneConflictType.Peace);
        _targetConflict.SetState(ZoneConflictType.Peace);
        var npc = new Unit { ObjId = 3, Faction = new() { Id = FactionsEnum.Hostile } };
        await Assert.That(PeaceProtection.PreventsAttack(npc, _target)).IsFalse();
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, npc)).IsFalse();
        await Assert.That(PeaceProtection.PreventsAttack(_target, _target)).IsFalse();
    }

    [Test]
    public async Task PreventsAttack_OfflineOwnerStructures_KeepPeaceProtection()
    {
        BaseUnit[] structures =
        [
            new House { OwnerId = 2 },
            new Slave { OwnerId = 2 },
            new Shipyard { ShipyardData = new() { Type2 = 2 } }
        ];
        _targetConflict.SetState(ZoneConflictType.Peace);
        foreach (var structure in structures)
        {
            structure.Faction = _target.Faction;
            structure.Transform.ZoneId = 200;
            await Assert.That(PeaceProtection.PreventsAttack(_attacker, structure)).IsTrue();
        }
    }

    [Test]
    public async Task PreventsAttack_OwnedRetribution_AppliesToMatesAndSlavesButNotHouses()
    {
        _target.Faction = _attacker.Faction;
        SetRetribution(_target);
        var mate = new Mate { ObjId = 20, OwnerObjId = _target.ObjId, Faction = _target.Faction };
        var slave = new Slave { ObjId = 21, OwnerId = _target.Id, Summoner = _target, Faction = _target.Faction };
        var house = new House { ObjId = 22, OwnerId = _target.Id, Faction = _target.Faction };
        MakeWorld(_attacker, _target, mate, slave, house);
        _targetConflict.SetState(ZoneConflictType.Peace);
        foreach (var owned in new Unit[] { mate, slave, house })
            SetField(owned.Transform, "_zoneId", 200u);

        await Assert.That(PeaceProtection.PreventsAttack(_attacker, mate)).IsFalse();
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, slave)).IsFalse();
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, house)).IsTrue();
    }

    [Test]
    public async Task PreventsAttack_DemolishedHouse_DoesNotKeepPlayerProtection()
    {
        var house = new House { OwnerId = 0, Faction = _target.Faction };
        house.Transform.ZoneId = 200;
        _targetConflict.SetState(ZoneConflictType.Peace);
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, house)).IsFalse();
    }

    [Test]
    public async Task PreventsAttack_OwnedUnitLevel_DoesNotUseTheCharacterLevelGuard()
    {
        var owned = new OwnedUnit(_attacker) { Level = 1, Faction = _attacker.Faction };
        owned.Transform.ZoneId = 100;
        _sourceConflict.SetState(ZoneConflictType.Peace);
        await Assert.That(PeaceProtection.PreventsAttack(owned, _target)).IsFalse();
    }

    [Test]
    public async Task PreventsAttack_BothAlliancesSelector_DoesNotProtectPirates()
    {
        _target.Faction = FactionManager.Instance.GetFaction(FactionsEnum.Pirate);
        _targetConflict.PeaceProtectedFactionId = 4;
        _targetConflict.SetState(ZoneConflictType.Peace);
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsFalse();
    }

    [Test]
    public async Task PreventsAttack_Duel_OnlyExemptsTheActivePair()
    {
        _targetConflict.SetState(ZoneConflictType.Peace);
        var duel = new Duel(_attacker, _target);
        SetField(_duels, "_duels", new ConcurrentDictionary<uint, Duel>(new Dictionary<uint, Duel> { [1] = duel, [2] = duel }));
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsTrue();
        duel.DuelEndTimerTask = new DuelEndTimerTask(duel, 1);
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsFalse();
        var mate = new Mate { ObjId = 20, OwnerObjId = _target.ObjId, Faction = _target.Faction };
        var slave = new Slave { ObjId = 21, OwnerId = _target.Id, Summoner = _target, Faction = _target.Faction };
        MakeWorld(_attacker, _target, mate, slave);
        SetField(mate.Transform, "_zoneId", 200u);
        SetField(slave.Transform, "_zoneId", 200u);
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, mate)).IsFalse();
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, slave)).IsTrue();
        var other = CreateCharacter(3, 100, FactionsEnum.NuiaAlliance);
        await Assert.That(PeaceProtection.PreventsAttack(other, _target)).IsTrue();
        duel.DuelEndTimerTask = null;
        await Assert.That(PeaceProtection.PreventsAttack(_attacker, _target)).IsTrue();
    }

    [Test]
    public async Task ReduceCurrentHp_PeaceStartsAfterCast_BlocksCharacterAndOwnedUnitDamage()
    {
        var owned = new OwnedUnit(_target) { ObjId = 20, Hp = 100, Level = 50, Faction = _target.Faction };
        owned.Transform.ZoneId = 200;
        owned.ReduceCurrentHp(_attacker, 10);
        await Assert.That(owned.Hp).IsEqualTo(90);

        _targetConflict.SetState(ZoneConflictType.Peace);
        owned.ReduceCurrentHp(_attacker, 10);
        _target.ReduceCurrentHp(_attacker, 10);
        await Assert.That(owned.Hp).IsEqualTo(90);
        await Assert.That(_target.Hp).IsEqualTo(100);

        _targetConflict.SetState(ZoneConflictType.War);
        owned.ReduceCurrentHp(_attacker, 10);
        await Assert.That(owned.Hp).IsEqualTo(80);
    }

    [Test]
    public async Task DamageEffect_ProtectedTarget_StopsBeforeDamageAndCombatState()
    {
        _targetConflict.SetState(ZoneConflictType.Peace);
        new DamageEffect().Apply(_attacker, null, _target, null, null, null, null, DateTime.UtcNow);
        await Assert.That(_target.Hp).IsEqualTo(100);
        await Assert.That(_target.IsInBattle).IsFalse();
        await Assert.That(_target.IsActivelyHostile(_attacker)).IsFalse();
    }

    [Test]
    public async Task GetInitialTarget_HostileSkill_RejectsProtectedTarget()
    {
        MakeWorld(_attacker, _target);
        var skill = new Skill(new SkillTemplate { TargetType = SkillTargetType.Hostile });
        var getTarget = typeof(Skill).GetMethod("GetInitialTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object[] args = [_attacker, new SkillCasterUnit(1), new SkillCastUnitTarget(2)];
        await Assert.That(getTarget.Invoke(skill, args)).IsEqualTo(_target);
        _targetConflict.SetState(ZoneConflictType.Peace);
        await Assert.That(getTarget.Invoke(skill, args)).IsNull();
    }

    private ZoneConflict CreateConflict()
    {
        var conflict = new ZoneConflict(_clock, _ => { }) { ConflictMin = 1, WarMin = 1, PeaceMin = 1, PeaceProtectedFactionId = 5 };
        conflict.Restore(null);
        return conflict;
    }

    private static void MakeWorld(params BaseUnit[] units)
    {
        var world = new WorldInstance(null, 0, true, 0);
        SetField(world, "_objects", new ConcurrentDictionary<uint, GameObject>(units.ToDictionary(unit => unit.ObjId, unit => (GameObject)unit)));
        SetField(world, "_baseUnits", new ConcurrentDictionary<uint, BaseUnit>(units.ToDictionary(unit => unit.ObjId)));
        SetField(world, "_units", new ConcurrentDictionary<uint, Unit>(units.OfType<Unit>().ToDictionary(unit => unit.ObjId)));
        foreach (var unit in units)
        {
            SetField(unit.Transform, "_instanceId", world.Id);
            unit.ParentWorld = world;
        }
    }

    private static CharacterMock CreateCharacter(uint id, uint zone, FactionsEnum faction)
    {
        var character = new CharacterMock { Id = id, ObjId = id, Level = 50, Hp = 100, Faction = FactionManager.Instance.GetFaction(faction) };
        character.Transform.ZoneId = zone;
        return character;
    }

    private static void SetRetribution(Character character)
    {
        var buffs = Mock.Of<IBuffs>();
        buffs.CheckBuff((uint)BuffConstants.Retribution).Returns(true);
        character.Buffs = buffs.Object;
    }

    private void InstallSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        _restore.Add(() => field.SetValue(null, previous));
        field.SetValue(null, value);
    }

    private static void SetField(object value, string name, object replacement)
    {
        value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, replacement);
    }

    private sealed class OwnedUnit(Character owner) : Unit
    {
        public override Character GetOwnerCharacter() => owner;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void PostUpdateCurrentHp(BaseUnit attackerBase, int oldHpValue, int newHpValue, KillReason killReason) { }
    }
}
