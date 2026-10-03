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
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.NpcGroup;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public sealed class NpcAggroLinkTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private SqliteConnection _connection;
    private NpcAggroLinkGameData _links;
    private WorldInstance _world;
    private Region _region;
    private QuietNpc _source;
    private QuietNpc _helper;
    private Character _abuser;
    private ProbeBehavior _behavior;

    [Before(Test)]
    public void SetUp()
    {
        var worlds = new WorldManager(null, null, null, null, null);
        ReplaceSingleton(worlds);
        ReplaceSingleton(new DuelManager());
        ReplaceSingleton(new QuestManager(null, null));
        ReplaceSingleton(new NpcGameData());
        ReplaceSingleton(new UnitAttributeLimitsGameData());
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        ReplaceSingleton(models);
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
        _world = new WorldInstance(new WorldTemplate { Id = 1, CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 1);
        var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worlds)!;
        instances[_world.Id] = _world;
        _region = new Region(_world, 0, 0, 0);
        SetField(_region, "_neighbors", new[] { _region });
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Execute("""
            CREATE TABLE aggro_links (id INTEGER);
            CREATE TABLE npcs (id INTEGER);
            CREATE TABLE npc_aggro_links (id INTEGER, npc_id INTEGER, aggro_link_id INTEGER);
            INSERT INTO aggro_links VALUES (109), (99);
            INSERT INTO npcs VALUES (3690), (10095), (999);
            INSERT INTO npc_aggro_links VALUES (1, 3690, 109), (2, 10095, 109), (3, 999, 99);
            """);
        _links = new NpcAggroLinkGameData();
        _links.Load(_connection);
        ReplaceSingleton(_links);
        _source = CreateNpc(1, 3690, 0);
        _source.Template.AcceptAggroLink = false;
        _helper = CreateNpc(2, 10095, 10);
        _abuser = new Character(null)
        {
            Id = 20, ObjId = 20, Hp = 100, ParentWorld = _world,
            IsVisible = true, Faction = new SystemFaction { Id = FactionsEnum.Hostile }
        };
        _world.AddObject(_abuser);
        _behavior = new ProbeBehavior { Ai = _source.Ai };
    }

    [After(Test)]
    public void TearDown()
    {
        _connection?.Dispose();
        if (_world != null)
            GC.SuppressFinalize(_world);
        foreach (var (field, previous) in _singletons)
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    public async Task AuthoredPassiveHelper_AcceptsLinkWhenSourceAttackScaleIsSmall()
    {
        _source.Template.AttackStartRangeScale = 0.001f;
        _helper.Template.Aggression = false;
        _helper.Template.AggroLinkSpecialRuleId = AggroLinkSpecialRuleKind.None;

        _behavior.UpdateAggroHelp(_abuser);
        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable[_abuser.ObjId].TotalAggro).IsEqualTo(1);
        await Assert.That(_abuser.IsInAggroListOf.ContainsKey(_helper.ObjId)).IsTrue();
        await Assert.That(_abuser.Events.OnHealed.GetInvocationList()
            .Count(handler => ReferenceEquals(handler.Target, _helper))).IsEqualTo(1);
    }

    [Test]
    public async Task ExactCompact_Link109UsesPassiveWizardReceiverAndRejectsReverseHelp()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrWhiteSpace(compact), "Set AAEMU_COMBAT_TEST_COMPACT for the authored NPC assistance test.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        _links.Load(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM npcs WHERE id IN (3690, 10095)";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        var rows = 0;
        while (reader.Read())
        {
            var npc = reader.GetUInt32("id") == _source.TemplateId ? _source : _helper;
            npc.Template.AcceptAggroLink = reader.GetBoolean("accept_aggro_link", true);
            npc.Template.Aggression = reader.GetBoolean("aggression", true);
            npc.Template.AggroLinkHelpDist = reader.GetFloat("aggro_link_help_dist");
            npc.Template.AggroLinkSightCheck = reader.GetBoolean("aggro_link_sight_check", true);
            npc.Template.AggroLinkSpecialRuleId = (AggroLinkSpecialRuleKind)reader.GetUInt32("aggro_link_special_rule_id");
            rows++;
        }
        await Assert.That(rows).IsEqualTo(2);
        await Assert.That(_source.Template.AcceptAggroLink).IsFalse();
        await Assert.That(_helper.Template.AcceptAggroLink).IsTrue();
        await Assert.That(_helper.Template.Aggression).IsFalse();
        await Assert.That(_helper.Template.AggroLinkSightCheck).IsFalse();
        await Assert.That(_helper.Template.AggroLinkSpecialRuleId).IsEqualTo(AggroLinkSpecialRuleKind.None);
        _helper.Transform.Local.SetPosition(1, 0, 0);

        _behavior.UpdateAggroHelp(_abuser);
        new ProbeBehavior { Ai = _helper.Ai }.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable[_abuser.ObjId].TotalAggro).IsEqualTo(1);
        await Assert.That(_source.AggroTable).IsEmpty();
    }

    [Test]
    public async Task ReceiverAcceptFlag_MakesTheSameAuthoredLinkDirectional()
    {
        _behavior.UpdateAggroHelp(_abuser);
        var reverse = new ProbeBehavior { Ai = _helper.Ai };

        reverse.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable).HasSingleItem();
        await Assert.That(_source.AggroTable).IsEmpty();
    }

    [Test]
    public async Task AggressiveUnlinkedHelper_DoesNotHelpBecauseOfAggressionAlone()
    {
        _helper.TemplateId = 999;
        _helper.Template.Aggression = true;

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable).IsEmpty();
    }

    [Test]
    [Arguments(10f, true)]
    [Arguments(9.99f, false)]
    [Arguments(0f, false)]
    [Arguments(-1f, false)]
    [Arguments(float.NaN, false)]
    [Arguments(float.PositiveInfinity, false)]
    public async Task ReceiverDistance_UsesAuthoredBoundary(float distance, bool expected)
    {
        _helper.Template.AggroLinkHelpDist = distance;

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable.ContainsKey(_abuser.ObjId)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false, false, true)]
    [Arguments(true, false, false)]
    [Arguments(true, true, true)]
    public async Task OptionalSight_ChecksTargetVisibilityOnlyWhenEnabled(bool checkSight, bool visible, bool expected)
    {
        _helper.Template.AggroLinkSightCheck = checkSight;
        _abuser.IsVisible = visible;

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable.ContainsKey(_abuser.ObjId)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(AggroLinkSpecialRuleKind.FactionHelp, RelationState.Friendly, true, true)]
    [Arguments(AggroLinkSpecialRuleKind.FactionHelp, RelationState.Friendly, false, false)]
    [Arguments(AggroLinkSpecialRuleKind.FriendlyHelp, RelationState.Friendly, false, true)]
    [Arguments(AggroLinkSpecialRuleKind.FriendlyHelp, RelationState.Hostile, false, false)]
    [Arguments(AggroLinkSpecialRuleKind.NeutralHelp, RelationState.Neutral, false, true)]
    [Arguments(AggroLinkSpecialRuleKind.NeutralHelp, RelationState.Friendly, false, false)]
    [Arguments(AggroLinkSpecialRuleKind.EveryoneHelp, RelationState.Hostile, false, true)]
    public async Task OrdinarySpecialRule_PreservesItsSourceFactionCheck(
        AggroLinkSpecialRuleKind rule, RelationState relation, bool sameFaction, bool expected)
    {
        _helper.TemplateId = 999;
        _helper.Template.AggroLinkSpecialRuleId = rule;
        if (!sameFaction)
            _helper.Faction = new SystemFaction { Id = (FactionsEnum)1001 };
        _helper.Faction.Relations[_source.Faction.Id] = new FactionRelation { State = relation };

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable.ContainsKey(_abuser.ObjId)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(NpcGroupAggroRuleKind.None, true, false)]
    [Arguments(NpcGroupAggroRuleKind.AggroLink, true, true)]
    [Arguments(NpcGroupAggroRuleKind.AggroShare, true, true)]
    [Arguments(NpcGroupAggroRuleKind.AggroLink, false, false)]
    [Arguments(NpcGroupAggroRuleKind.AggroShare, false, false)]
    public async Task RuntimeGroup_UsesTheSameActiveOccurrenceOnly(
        NpcGroupAggroRuleKind rule, bool sameOccurrence, bool expected)
    {
        _helper.TemplateId = 999;
        var sourceGroup = CreateGroup(rule);
        sourceGroup.Attach(new NpcGroupMember { Id = 1, NpcGroupId = 50, NpcId = (int)_source.TemplateId }, _source);
        var helperGroup = sameOccurrence ? sourceGroup : CreateGroup(rule);
        helperGroup.Attach(new NpcGroupMember { Id = 2, NpcGroupId = 50, NpcId = (int)_helper.TemplateId }, _helper);

        await Assert.That(NpcAggroLink.CanHelp(_source, _helper, _abuser)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("accept")]
    [Arguments("distance")]
    [Arguments("sight")]
    [Arguments("retired")]
    public async Task RuntimeGroup_DoesNotBypassReceiverRulesOrRetirement(string gate)
    {
        _helper.TemplateId = 999;
        var group = CreateGroup(NpcGroupAggroRuleKind.AggroLink);
        group.Attach(new NpcGroupMember { Id = 1, NpcGroupId = 50, NpcId = (int)_source.TemplateId }, _source);
        group.Attach(new NpcGroupMember { Id = 2, NpcGroupId = 50, NpcId = (int)_helper.TemplateId }, _helper);
        switch (gate)
        {
            case "accept": _helper.Template.AcceptAggroLink = false; break;
            case "distance": _helper.Template.AggroLinkHelpDist = 1; break;
            case "sight":
                _helper.Template.AggroLinkSightCheck = true;
                _abuser.IsVisible = false;
                break;
            case "retired": group.Retire(); break;
        }

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable).IsEmpty();
    }

    [Test]
    public async Task RuntimeGroupLink_AlertsItsMemberThroughTheBehaviorPath()
    {
        _helper.TemplateId = 999;
        var group = CreateGroup(NpcGroupAggroRuleKind.AggroLink);
        group.Attach(new NpcGroupMember { Id = 1, NpcGroupId = 50, NpcId = (int)_source.TemplateId }, _source);
        group.Attach(new NpcGroupMember { Id = 2, NpcGroupId = 50, NpcId = (int)_helper.TemplateId }, _helper);

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable[_abuser.ObjId].TotalAggro).IsEqualTo(1);
    }

    [Test]
    public async Task LinkedHelper_DoesNotAttackItsFriendlyTarget()
    {
        _abuser.Faction = new SystemFaction { Id = FactionsEnum.Friendly };

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable).IsEmpty();
    }

    [Test]
    [Arguments("source-dead")]
    [Arguments("helper-dead")]
    [Arguments("abuser-dead")]
    [Arguments("helper-despawned")]
    [Arguments("source-removed")]
    [Arguments("helper-removed")]
    [Arguments("abuser-removed")]
    [Arguments("helper-replaced")]
    [Arguments("helper-ai-null")]
    [Arguments("helper-ai-retired")]
    [Arguments("source-ai-null")]
    public async Task RetiredOrMissingObject_DoesNotAcquireAggro(string state)
    {
        switch (state)
        {
            case "source-dead": _source.Hp = 0; break;
            case "helper-dead": _helper.Hp = 0; break;
            case "abuser-dead": _abuser.Hp = 0; break;
            case "helper-despawned": _helper.Despawned = true; break;
            case "source-removed": _world.RemoveObject(_source); break;
            case "helper-removed": _world.RemoveObject(_helper); break;
            case "abuser-removed": _world.RemoveObject(_abuser); break;
            case "helper-replaced":
                _world.RemoveObject(_helper);
                _world.AddObject(new Unit { ObjId = _helper.ObjId, ParentWorld = _world, Hp = 100 });
                break;
            case "helper-ai-null": _helper.Ai = null; break;
            case "helper-ai-retired": _helper.Ai.Owner = null; break;
            case "source-ai-null": _source.Ai = null; break;
        }

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DifferentWorld_DoesNotAcquireAggro(bool helperInOtherWorld)
    {
        var other = new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 2);
        GC.SuppressFinalize(other);
        Unit moved = helperInOtherWorld ? _helper : _abuser;
        _world.RemoveObject(moved);
        moved.ParentWorld = other;
        other.AddObject(moved);

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task EngagedHelper_DoesNotReceiveNewInitialThreat(bool combatFlag)
    {
        if (combatFlag)
            _helper.IsInBattle = true;
        else
            _helper.AddUnitAggro(AggroKind.Heal, _source, 10);

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable.ContainsKey(_abuser.ObjId)).IsFalse();
    }

    [Test]
    public async Task ConcurrentHelpRequests_AddOneThreatEntryAndOneSubscription()
    {
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => _behavior.UpdateAggroHelp(_abuser))));

        await Assert.That(_helper.AggroTable[_abuser.ObjId].TotalAggro).IsEqualTo(1);
        await Assert.That(_abuser.Events.OnHealed.GetInvocationList()
            .Count(handler => ReferenceEquals(handler.Target, _helper))).IsEqualTo(1);
    }

    [Test]
    public async Task AggroEvent_RemovesHelperWithoutCallingItsRetiredAi()
    {
        _abuser.Events.OnAggro += (_, _) => _helper.Delete();

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.Ai).IsNull();
        await Assert.That(_helper.AggroTable).IsEmpty();
        await Assert.That(_abuser.IsInAggroListOf).IsEmpty();
        await Assert.That(_abuser.Events.OnHealed.GetInvocationList()
            .Any(handler => ReferenceEquals(handler.Target, _helper))).IsFalse();
    }

    [Test]
    public async Task Reload_RemovedMembershipStopsFutureAssistance()
    {
        _behavior.UpdateAggroHelp(_abuser);
        await Assert.That(_helper.AggroTable).HasSingleItem();
        _helper.ClearAllAggro();
        _helper.IsInBattle = false;
        Execute("DELETE FROM npc_aggro_links WHERE npc_id = 10095;");
        _links.Load(_connection);

        _behavior.UpdateAggroHelp(_abuser);

        await Assert.That(_helper.AggroTable).IsEmpty();
    }

    [Test]
    public async Task Assistance_DefersAndCombinesAiTransitionsUntilReceiverTick()
    {
        var ai = new TransitionProbeAi { Owner = _helper };
        _helper.Ai = ai;
        ai.Start();

        _behavior.UpdateAggroHelp(_abuser);
        ai.RequestAggroTargetUpdate();
        ai.RequestAggroTargetUpdate();
        await Assert.That(ai.AttackEntries).IsEqualTo(0);

        ai.Tick(TimeSpan.FromMilliseconds(100));
        ai.Tick(TimeSpan.FromMilliseconds(100));

        await Assert.That(ai.AttackEntries).IsEqualTo(1);
    }

    [Test]
    public async Task DeferredAiTransition_StopsTheTickWhenItsCallbackDeletesTheOwner()
    {
        var ai = new TransitionProbeAi { Owner = _helper, OnAttackEntry = () => _helper.Delete() };
        _helper.Ai = ai;
        ai.Start();
        _behavior.UpdateAggroHelp(_abuser);

        ai.Tick(TimeSpan.FromMilliseconds(100));

        await Assert.That(ai.AttackEntries).IsEqualTo(1);
        await Assert.That(ai.Owner).IsNull();
        await Assert.That(_helper.AggroTable).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DeferredAiTransition_DoesNotRunAfterOwnerDiesOrDetaches(bool dead)
    {
        var ai = new TransitionProbeAi { Owner = _helper };
        _helper.Ai = ai;
        ai.Start();
        _behavior.UpdateAggroHelp(_abuser);
        if (dead)
            _helper.Hp = 0;
        else
            ai.Owner = null;

        ai.Tick(TimeSpan.FromMilliseconds(100));

        await Assert.That(ai.AttackEntries).IsEqualTo(0);
    }

    private NpcGroupInstance CreateGroup(NpcGroupAggroRuleKind rule) => new(
        new NpcGroup { Id = 50, AggroRuleId = (int)rule },
        new NpcSpawner { ParentWorld = _world }, new WorldSpawnPosition());

    private QuietNpc CreateNpc(uint objectId, uint templateId, float x)
    {
        var npc = new QuietNpc
        {
            ObjId = objectId, TemplateId = templateId, ParentWorld = _world, Hp = 100,
            Template = new NpcTemplate { AcceptAggroLink = true, AggroLinkHelpDist = 15 },
            Faction = new SystemFaction { Id = (FactionsEnum)1000 }
        };
        npc.Transform.Local.SetPosition(x, 0, 0);
        npc.Ai = new ProbeAi { Owner = npc };
        npc.Region = _region;
        _world.AddObject(npc);
        _region.AddObject(npc);
        return npc;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
        public override float ModelSize => 0;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class ProbeAi : NpcAi
    {
        protected override void Build() { }
    }

    private sealed class TransitionProbeAi : NpcAi
    {
        public int AttackEntries { get; private set; }
        public Action OnAttackEntry { get; init; }
        protected override void Build()
        {
            var idle = AddBehavior(BehaviorKind.Idle, new ProbeBehavior());
            AddBehavior(BehaviorKind.Attack, new ProbeBehavior
            {
                OnEnter = () =>
                {
                    AttackEntries++;
                    OnAttackEntry?.Invoke();
                }
            });
            idle.AddTransition(TransitionEvent.OnAggroTargetChanged, BehaviorKind.Attack);
            SetCurrentBehavior(BehaviorKind.Idle);
        }
    }

    private sealed class ProbeBehavior : Behavior
    {
        public Action OnEnter { get; init; }
        public override void Enter() => OnEnter?.Invoke();
        public override void Tick(TimeSpan delta) { }
        public override void Exit() { }
    }
}
