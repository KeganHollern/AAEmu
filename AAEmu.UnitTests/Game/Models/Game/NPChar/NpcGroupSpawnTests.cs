using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.NpcGroup;
using AAEmu.Game.Models.Game.Schedules;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public sealed class NpcGroupSpawnTests
{
    private static readonly FieldInfo s_instance = typeof(Singleton<NpcGroupGameData>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previous;
    private SqliteConnection _connection;
    private NpcGroupGameData _data;
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private WorldInstance _world;
    private uint _previousDefaultInstanceId;

    [Before(Test)]
    public void SetUp()
    {
        _previous = s_instance.GetValue(null);
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Execute("""
            CREATE TABLE npc_groups (id INTEGER, name TEXT, aggro_rule_id INTEGER, enable_respawn TEXT);
            CREATE TABLE npc_group_members (id INTEGER, npc_group_id INTEGER, npc_id INTEGER,
                is_leader TEXT, is_move_leader TEXT, formation_offset_x REAL, formation_offset_y REAL,
                formation_offset_z REAL, formation_tension REAL);
            INSERT INTO npc_groups VALUES (50, 'Calm Sea mother and babies', 1, 'f');
            INSERT INTO npc_group_members VALUES
                (191, 50, 8564, 't', 't', 0, 0, 0, 2),
                (192, 50, 8566, 'f', 'f', 2, -3, 0, 1),
                (193, 50, 8566, 'f', 'f', -2, -3, 0, 1);
            """);
        _data = new NpcGroupGameData();
        _data.Load(_connection);
        s_instance.SetValue(null, _data);
        Replace(new GameScheduleManager(null, TimeProvider.System));
        Replace(new NpcGameData());
        Replace(new WorldManager(null, null, null, null, null));
        var ids = new ObjectIdManager();
        ids.Initialize();
        var idsField = typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((idsField, idsField.GetValue(null)));
        idsField.SetValue(null, ids);
        _previousDefaultInstanceId = WorldManager.DefaultInstanceId;
        WorldManager.DefaultInstanceId = 0;
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 0);
        _world.SpawnManager = new SpawnManager(_world);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(WorldManager.Instance)!;
        worlds[_world.Id] = _world;
    }

    [Test]
    public async Task RedDragonEgg_BindsOnlyTheCapturedBossToItsInactiveEffectPlacement()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "npc_spawns.json");
        var captures = JsonHelper.DeserializeObject<List<NpcSpawner>>(await File.ReadAllTextAsync(path));
        var capture = captures.Single(record => record.UnitId == 12411);
        _world.SpawnManager.AddNpcSpawner(capture);

        var dragon = _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(120194).Single();
        await Assert.That(dragon.Template.Npcs.Single().MemberId).IsEqualTo(12411u);
        await Assert.That(dragon.IsActive).IsFalse();
        await Assert.That(dragon.IsRuntimeActivated).IsFalse();
        await Assert.That(_world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(13759)).IsEmpty();

        var definition = new ProbeDefinition { NpcSpawnerTemplateId = dragon.SpawnerId, MemberType = "Npc", MemberId = 12411 };
        definition.Position = capture.Position;
        dragon.Template.Npcs = [definition];
        dragon.InitializeSpawnableNpcs(dragon.Template);
        dragon.Update();
        await Assert.That(definition.Published).IsEmpty();

        var caster = new ProbeNpc { ParentWorld = _world, Hp = 100 };
        var effect = new NpcSpawnerSpawnEffect { Id = 2668, SpawnerId = 120194 };
        effect.Apply(caster, null, caster, null, null, null, null, DateTime.UtcNow);
        var summoned = definition.Published.Single();
        await Assert.That(summoned.TemplateId).IsEqualTo(12411u);
        await Assert.That(dragon.IsRuntimeActivated).IsFalse();
        summoned.Hp = 0;
        dragon.DoDespawn(summoned);
        dragon.Despawn(summoned);
        dragon.Update();
        await Assert.That(definition.Positions.Count).IsEqualTo(1);
    }

    [After(Test)]
    public void TearDown()
    {
        s_instance.SetValue(null, _previous);
        _connection.Dispose();
        WorldManager.DefaultInstanceId = _previousDefaultInstanceId;
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    public async Task Group50_UsesThreeMemberRowsAndPublishesAllBeforeSpawnEvents()
    {
        var definition = Definition();
        var spawner = Spawner();

        var members = definition.Spawn(spawner, 42);

        await Assert.That(members.Select(npc => npc.TemplateId)).IsEquivalentTo(new uint[] { 8564, 8566, 8566 });
        await Assert.That(members.All(npc => npc.OwnerId == 42)).IsTrue();
        await Assert.That(members.Select(npc => npc.GroupMember.Id)).IsEquivalentTo(new[] { 191, 192, 193 });
        await Assert.That(members.Select(npc => npc.GroupInstance).Distinct().Count()).IsEqualTo(1);
        await Assert.That(members[0].GroupInstance.Leader).IsSameReferenceAs(members[0]);
        await Assert.That(members[0].GroupInstance.MoveLeader).IsSameReferenceAs(members[0]);
        await Assert.That(definition.CompleteSnapshots).IsEquivalentTo(new[] { 3, 3, 3 });
        await Assert.That(definition.Published.Count).IsEqualTo(3);
        await Assert.That(definition.Positions.Select(position => (position.X, position.Y, position.Z)))
            .IsEquivalentTo(new[] { (100f, 200f, 30f), (102f, 197f, 30f), (98f, 197f, 30f) });
        await Assert.That((spawner.Position.X, spawner.Position.Y, spawner.Position.Z)).IsEqualTo((100f, 200f, 30f));
    }

    [Test]
    public async Task ExactClient_Group50UsesTheAuthoredMotherAndTwoBabies()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrWhiteSpace(compact), "Set AAEMU_COMBAT_TEST_COMPACT for the authored NPC group test.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        _data.Load(connection);

        var members = Definition().Spawn(Spawner());

        await Assert.That(members.Select(npc => npc.TemplateId)).IsEquivalentTo(new uint[] { 8564, 8566, 8566 });
        await Assert.That(members[0].GroupInstance.Template.EnableRespawn).IsFalse();
        await Assert.That(members[0].GroupInstance.Template.AggroRuleId).IsEqualTo(1);
        await Assert.That(members.Select(npc => npc.GroupMember.Id)).IsEquivalentTo(new[] { 191, 192, 193 });
    }

    [Test]
    public async Task TwoOccurrences_KeepIndependentMembersAndDetachOnlyTheOwnedObject()
    {
        var definition = Definition();
        var first = definition.Spawn(Spawner());
        var second = definition.Spawn(Spawner());
        var firstGroup = first[0].GroupInstance;
        var secondGroup = second[0].GroupInstance;

        firstGroup.Detach(first[1]);
        firstGroup.Detach(second[1]);

        await Assert.That(firstGroup).IsNotSameReferenceAs(secondGroup);
        await Assert.That(firstGroup.GetMembers().Length).IsEqualTo(2);
        await Assert.That(first[1].GroupInstance).IsNull();
        await Assert.That(first[1].GroupMember).IsNull();
        await Assert.That(secondGroup.GetMembers().Length).IsEqualTo(3);
        await Assert.That(second[1].GroupInstance).IsSameReferenceAs(secondGroup);
    }

    [Test]
    public async Task Population_CountsEachGroupOccurrenceOnce()
    {
        var definition = Definition();
        var first = definition.Spawn(Spawner());
        var second = definition.Spawn(Spawner());
        var ordinary = new Npc { TemplateId = 50 };

        await Assert.That(NpcSpawner.CountSpawnedOccurrences(first)).IsEqualTo(1);
        await Assert.That(NpcSpawner.CountSpawnedOccurrences(first.Concat(second).Append(ordinary))).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartialDeath_RefillsOnlyEnabledGroupsAfterDelayAndCorpseRemoval(bool enabled)
    {
        _data.GetNpcGroup(50).EnableRespawn = enabled;
        var (spawner, definition, members) = SpawnTrackedGroup();
        // A later occurrence can select another delay on this shared spawner.
        spawner.RespawnTime = 999;
        var death = DateTime.UtcNow;
        members[1].Hp = 0;
        members[1].DeadTime = death;
        spawner.DoDespawn(members[1]);
        spawner.ProcessGroupRespawns(death.AddSeconds(61));
        await Assert.That(definition.Positions.Count).IsEqualTo(3);
        spawner.Despawn(members[1]);
        spawner.ProcessGroupRespawns(death.AddSeconds(59));
        await Assert.That(definition.Positions.Count).IsEqualTo(3);

        spawner.ProcessGroupRespawns(death.AddSeconds(60));

        await Assert.That(definition.Positions.Count).IsEqualTo(enabled ? 4 : 3);
        await Assert.That(members[0].GroupInstance.GetMembers().Length).IsEqualTo(enabled ? 3 : 2);
        await Assert.That(spawner.SpawnedNpcs[9571].Count).IsEqualTo(enabled ? 3 : 2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FullWipe_ReplacesOneWholeGroupAfterTheLastDeathDelay(bool enabled)
    {
        _data.GetNpcGroup(50).EnableRespawn = enabled;
        var (spawner, definition, members) = SpawnTrackedGroup();
        var oldGroup = members[0].GroupInstance;
        var death = DateTime.UtcNow;
        for (var i = 0; i < members.Count; i++)
        {
            members[i].Hp = 0;
            members[i].DeadTime = death.AddSeconds(i);
            spawner.DoDespawn(members[i]);
            spawner.Despawn(members[i]);
        }
        spawner.ProcessGroupRespawns(death.AddSeconds(61));
        spawner.DoSpawn();
        await Assert.That(definition.Positions.Count).IsEqualTo(3);

        spawner.ProcessGroupRespawns(death.AddSeconds(62));
        spawner.DoSpawn();

        await Assert.That(definition.Positions.Count).IsEqualTo(6);
        await Assert.That(oldGroup.IsRetired).IsTrue();
        await Assert.That(spawner.SpawnedNpcs[9571].Count).IsEqualTo(3);
        await Assert.That(spawner.SpawnedNpcs[9571][0].GroupInstance).IsNotSameReferenceAs(oldGroup);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EffectRemoval_PreservesPartialAndWholeGroupRespawnRules(bool enabled)
    {
        _data.GetNpcGroup(50).EnableRespawn = enabled;
        var (spawner, definition, members) = SpawnTrackedGroup();
        var group = members[0].GroupInstance;

        spawner.DespawnFromEffect(members[1]);
        spawner.DespawnFromEffect(members[1]);
        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddSeconds(61));

        await Assert.That(definition.Positions.Count).IsEqualTo(enabled ? 4 : 3);
        await Assert.That(group.GetMembers().Length).IsEqualTo(enabled ? 3 : 2);
        foreach (var member in group.GetMembers())
            spawner.DespawnFromEffect(member);
        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddSeconds(61));
        spawner.DoSpawn();

        await Assert.That(group.IsRetired).IsTrue();
        await Assert.That(definition.Positions.Count).IsEqualTo(enabled ? 7 : 6);
        await Assert.That(spawner.SpawnedNpcs[9571].Count).IsEqualTo(3);
    }

    [Test]
    public async Task ExplicitReset_CancelsPendingMemberReplacement()
    {
        _data.GetNpcGroup(50).EnableRespawn = true;
        var (spawner, definition, members) = SpawnTrackedGroup();
        var group = members[0].GroupInstance;
        members[1].Hp = 0;
        spawner.DoDespawn(members[1]);
        spawner.DespawnAll();

        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));

        await Assert.That(definition.Positions.Count).IsEqualTo(3);
        await Assert.That(group.IsRetired).IsTrue();
        await Assert.That(group.GetMembers()).IsEmpty();
        await Assert.That(spawner.SpawnedNpcs).IsEmpty();
    }

    [Test]
    public async Task WorldTeardown_RetiresGroupsAndCancelsPendingRefill()
    {
        _data.GetNpcGroup(50).EnableRespawn = true;
        var (spawner, definition, members) = SpawnTrackedGroup();
        var group = members[0].GroupInstance;
        members[1].Hp = 0;
        spawner.DoDespawn(members[1]);
        var registered = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_world.SpawnManager)!;
        registered.Add(1, [spawner]);
        _world.GimmickManager = new GimmickManager(_world);

        _world.SpawnManager.DeleteAllSpawners();
        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));

        await Assert.That(group.IsRetired).IsTrue();
        await Assert.That(group.GetMembers()).IsEmpty();
        await Assert.That(spawner.ParentWorld).IsNull();
        await Assert.That(spawner.IsActive).IsFalse();
        await Assert.That(definition.Positions.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ClosedSchedule_CancelsPendingMemberReplacement()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-03T12:00:00Z"));
        var schedules = new GameScheduleManager(null, time);
        schedules.LoadGameSchedules(new Dictionary<int, GameSchedules>
            { [1] = new() { Id = 1, StartTime = 12, EndTime = 13 } });
        schedules.LoadGameScheduleSpawners(new Dictionary<int, GameScheduleSpawners>
            { [1] = new() { Id = 1, GameScheduleId = 1, SpawnerId = 9571 } });
        Replace(schedules);
        _data.GetNpcGroup(50).EnableRespawn = true;
        var (spawner, definition, members) = SpawnTrackedGroup();
        var group = members[0].GroupInstance;
        members[1].Hp = 0;
        spawner.DoDespawn(members[1]);
        time.Advance(TimeSpan.FromHours(2));
        spawner.Update();
        time.Advance(TimeSpan.FromHours(22));

        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));

        await Assert.That(definition.Positions.Count).IsEqualTo(3);
        await Assert.That(group.IsRetired).IsTrue();
    }

    [Test]
    public async Task ExplicitGroupTemplate_PreservesMemberTypeAndDoesNotMutateTheCompactTemplate()
    {
        var source = new NpcSpawnerTemplate
        {
            Id = 9571, MaxPopulation = 1, SpawnDelayMin = 125, SpawnDelayMax = 125,
            Npcs = [Definition()]
        };
        var clone = SpawnManager.CloneExplicitGroupTemplate(source);
        clone.Npcs[0].MemberId = 52;

        await Assert.That(clone.Npcs[0].MemberType).IsEqualTo("NpcGroup");
        await Assert.That(clone.MaxPopulation).IsEqualTo(1u);
        await Assert.That(clone.SpawnDelayMin).IsEqualTo(125f);
        await Assert.That(source.Npcs[0].MemberId).IsEqualTo(50u);
    }

    [Test]
    [Arguments(0.5f, 0.5f)]
    [Arguments(0f, 1f)]
    [Arguments(1f, 0f)]
    public async Task MainWorldSinglePopulationPlacement_SelectsOneWeightedNpcAtItsAnchor(float vyravaWeight, float golemWeight)
    {
        var vyrava = new ProbeDefinition { Id = 1, NpcSpawnerTemplateId = 14399, MemberId = 12926, MemberType = "Npc", Weight = vyravaWeight };
        var golem = new ProbeDefinition { Id = 2, NpcSpawnerTemplateId = 14399, MemberId = 4334, MemberType = "Npc", Weight = golemWeight };
        var template = new NpcSpawnerTemplate { Id = 14399, MaxPopulation = 1, ActivationState = true, Npcs = [vyrava, golem] };
        NpcGameData.Instance.AddNpcSpawner(template);
        foreach (var member in template.Npcs)
            NpcGameData.Instance.AddMemberAndSpawnerTemplateIds(member);
        var anchor = new WorldSpawnPosition { X = 29943.201f, Y = 7065.4463f, Z = 408.3656f, Yaw = 1f };
        _world.SpawnManager.AddNpcSpawner(new NpcSpawner { UnitId = 12926, Position = anchor });
        var spawner = _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(14399).Single();

        for (var cycle = 0; cycle < 2; cycle++)
        {
            spawner.DoSpawn();
            spawner.DoSpawn();
            await Assert.That(spawner.SpawnedNpcs[14399]).HasSingleItem();
            var npc = spawner.SpawnedNpcs[14399].Single();
            var chosen = npc.TemplateId == 12926 ? vyrava : golem;
            await Assert.That(chosen.Weight).IsGreaterThan(0f);
            await Assert.That(npc.TemplateId).IsEqualTo(chosen.MemberId);
            await Assert.That(npc.Transform.World.Position).IsEqualTo(anchor.AsPositionVector());
            await Assert.That(npc.Transform.World.Rotation.Z).IsEqualTo(anchor.Yaw);
            spawner.Despawn(npc);
        }
        await Assert.That(vyrava.Positions.Count + golem.Positions.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task VyravaPlacement_RetainsItsOwnDeathDeadlineWhileBothOtherGiantsLive(bool repeatRemoval)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 6, 59, 0, TimeSpan.Zero));
        var config = new AppConfiguration
        {
            World = new WorldConfig { Time = new WorldTimeConfig { Mode = WorldTimeMode.TimeZone, TimeZoneId = "UTC" } }
        };
        var time = new TimeManager(null, null, clock, Options.Create(config));
        Replace(time);
        time.GetSnapshot();
        var anchor = new WorldSpawnPosition { X = 30197.527f, Y = 7188.55f, Z = 407.25623f };
        var player = new Character(null) { Id = 1, ObjId = 1, ParentWorld = _world };
        player.Transform.Local.SetPosition(anchor.AsPositionVector());
        WorldManager.Instance.TryAddCharacter(player);
        RegisterVyravaTemplate(14354, 10, false, false);
        RegisterVyravaTemplate(14399, 3600, true, true);
        RegisterVyravaTemplate(15025, 3600, true, false);
        _world.SpawnManager.AddNpcSpawner(new NpcSpawner { UnitId = 12926, Position = anchor });
        var weighted = _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(14399).Single();
        var timed = _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(15025).Single();
        var permanent = _world.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(14354).Single();
        weighted.DoSpawn();
        timed.DoSpawn();
        permanent.DoSpawn();
        await Assert.That(weighted.CurrentSpawnCount).IsZero();
        await Assert.That(timed.CurrentSpawnCount).IsZero();
        var permanentGiant = permanent.SpawnedNpcs[14354].Single();

        clock.Advance(TimeSpan.FromMinutes(1));
        weighted.DoSpawn();
        timed.DoSpawn();
        var victim = weighted.SpawnedNpcs[14399].Single();
        var timedGiant = timed.SpawnedNpcs[15025].Single();
        await Assert.That(victim.Spawner).IsSameReferenceAs(weighted);
        // This fixture substitutes the NPC model. Use the fixed delay assigned by stock NPC preparation.
        weighted.RespawnTime = (int)weighted.Template.SpawnDelayMin;
        victim.Hp = 0;
        victim.Template = new NpcTemplate();
        var items = new ItemManager(null, null, null, null, null, null);
        typeof(ItemManager).GetField("_lootPackDroppingNpc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(items, new Dictionary<uint, List<LootPackDroppingNpc>>());
        Replace(items);
        var previousWorldConfig = AppConfiguration.Instance.World;
        try
        {
            AppConfiguration.Instance.World = config.World;
            victim.DoDie(timedGiant, KillReason.Damage);
        }
        finally
        {
            AppConfiguration.Instance.World = previousWorldConfig;
        }
        if (repeatRemoval)
            weighted.DespawnWithRespawn(victim);
        _world.SpawnManager.DespawnObject(victim);
        var deadline = victim.DeadTime.AddHours(1);
        await Assert.That(victim.Respawn).IsEqualTo(deadline);
        await Assert.That(_world.SpawnManager.GetRespawnsReady(deadline.AddTicks(-1))).IsEmpty();
        weighted.Update();
        await Assert.That(weighted.CurrentSpawnCount).IsZero();

        var due = _world.SpawnManager.GetRespawnsReady(deadline).Single();
        await Assert.That(due).IsSameReferenceAs(victim);
        _world.SpawnManager.RespawnObject(due);
        weighted.Update();
        var replacement = weighted.SpawnedNpcs[14399].Single();
        weighted.Update();
        await Assert.That(_world.SpawnManager.GetRespawnsReady(deadline.AddHours(1))).IsEmpty();
        await Assert.That(weighted.SpawnedNpcs[14399].Single()).IsSameReferenceAs(replacement);
        await Assert.That(replacement).IsNotSameReferenceAs(victim);
        await Assert.That(replacement.Spawner).IsSameReferenceAs(weighted);
        await Assert.That(timed.SpawnedNpcs[15025].Single()).IsSameReferenceAs(timedGiant);
        await Assert.That(permanent.SpawnedNpcs[14354].Single()).IsSameReferenceAs(permanentGiant);

        // The replacement also owns a full delay. Close its window before that delay expires.
        replacement.Hp = 0;
        replacement.DeadTime = deadline.AddMinutes(1);
        weighted.DoDespawn(replacement);
        await Assert.That(replacement.Respawn).IsEqualTo(replacement.DeadTime.AddHours(1));
        _world.SpawnManager.DespawnObject(replacement);
        clock.Advance(TimeSpan.FromHours(10));
        await Assert.That(timed.CanDespawnNpcs()).IsTrue();
        weighted.Update();
        timed.Update();
        _world.SpawnManager.DespawnObject(timedGiant);
        var late = _world.SpawnManager.GetRespawnsReady(replacement.Respawn).Single();
        await Assert.That(late).IsSameReferenceAs(replacement);
        _world.SpawnManager.RespawnObject(late);
        weighted.Update();
        timed.Update();
        await Assert.That(weighted.CurrentSpawnCount).IsZero();
        await Assert.That(timed.CurrentSpawnCount).IsZero();
        await Assert.That(permanent.SpawnedNpcs[14354].Single()).IsSameReferenceAs(permanentGiant);
    }

    private static void RegisterVyravaTemplate(uint id, int delay, bool daylight, bool weighted)
    {
        var giant = new ProbeDefinition { NpcSpawnerTemplateId = id, MemberId = 12926, MemberType = "Npc", Weight = weighted ? .5f : 1f };
        var template = new NpcSpawnerTemplate
        {
            Id = id, MaxPopulation = 1, ActivationState = true, Npcs = [giant],
            StartTime = daylight ? 7 : 0, EndTime = daylight ? 16.6f : 0,
            TestRadiusPc = daylight ? 25 : 0, TestRadiusNpc = daylight ? 20 : 3,
            SuspendSpawnCount = daylight ? 1u : 0u, SpawnDelayMin = delay, SpawnDelayMax = delay
        };
        if (weighted)
            template.Npcs.Add(new ProbeDefinition { NpcSpawnerTemplateId = id, MemberId = 4334, MemberType = "Npc", Weight = .5f });
        NpcGameData.Instance.AddNpcSpawner(template);
        foreach (var member in template.Npcs)
            NpcGameData.Instance.AddMemberAndSpawnerTemplateIds(member);
    }

    [Test]
    public async Task ExplicitGroupPlacement_LoadsAuthoredMembersAndKeepsRuntimeActivationRules()
    {
        var source = new NpcSpawnerTemplate
        {
            Id = 9571, MaxPopulation = 1, SpawnDelayMin = 125, SpawnDelayMax = 125,
            ActivationState = true, Npcs = [Definition()]
        };
        NpcGameData.Instance.AddNpcSpawner(source);
        var spawner = Spawner();
        spawner.NpcSpawnerIds = [9571];

        _world.SpawnManager.AddNpcSpawner(spawner);

        await Assert.That(spawner.Template.Npcs.Single().MemberType).IsEqualTo("NpcGroup");
        await Assert.That(spawner.SpawnableNpcs.Single().MemberId).IsEqualTo(50u);
        await Assert.That(spawner.Template.SpawnDelayMin).IsEqualTo(125f);
        await Assert.That(spawner.ParentWorld).IsSameReferenceAs(_world);
        await Assert.That(spawner.IsRuntimeActivated).IsFalse();
        await Assert.That(source.Npcs.Single().Position).IsNotSameReferenceAs(spawner.Position);
        spawner.Activate();
        await Assert.That(spawner.IsRuntimeActivated).IsTrue();
    }

    [Test]
    public async Task RandomGroupSpawn_TracksEveryMemberAndPublishesOnlyOnce()
    {
        var definition = Definition();
        var spawner = Spawner();
        spawner.ParentWorld = _world;
        spawner.Template = new NpcSpawnerTemplate { Id = 9571, Npcs = [definition] };
        NpcGameData.Instance.AddNpcSpawner(spawner.Template);

        var result = spawner.DoRandomSpawn(9571, 42);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.OwnerId).IsEqualTo(42u);
        await Assert.That(spawner.SpawnedNpcs[9571].Count).IsEqualTo(3);
        await Assert.That(definition.Positions.Count).IsEqualTo(3);
        await Assert.That(definition.CompleteSnapshots.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(1u, 60)]
    [Arguments(0u, 0)]
    public async Task SuppressedRespawn_DoesNotRefillOrReplaceAnInstance(uint instanceId, int delay)
    {
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, instanceId);
        _world.SpawnManager = new SpawnManager(_world);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(WorldManager.Instance)!;
        worlds[_world.Id] = _world;
        _data.GetNpcGroup(50).EnableRespawn = true;
        var (spawner, definition, members) = SpawnTrackedGroup(delay);
        foreach (var npc in members)
        {
            npc.Hp = 0;
            spawner.DoDespawn(npc);
            spawner.Despawn(npc);
        }

        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));
        spawner.DoSpawn();

        await Assert.That(definition.Positions.Count).IsEqualTo(3);
    }

    [Test]
    public async Task EventOwnedGroup_DoesNotUseTheOrdinaryRespawnTimer()
    {
        _data.GetNpcGroup(50).EnableRespawn = true;
        var token = new TowerDefenseSpawnToken("occurrence", "event", "site", 1, 1, "action");
        var (spawner, definition, members) = SpawnTrackedGroup(60, token);
        foreach (var npc in members)
        {
            npc.Hp = 0;
            spawner.DoDespawn(npc);
            spawner.Despawn(npc);
        }

        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));
        spawner.DoSpawn();

        await Assert.That(definition.Positions.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitSpawn_ReplacesAnEmptySuppressedGroupWithoutAutomaticRepopulation(bool owned)
    {
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, owned ? 0u : 1u);
        _world.SpawnManager = new SpawnManager(_world);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(WorldManager.Instance)!;
        worlds[_world.Id] = _world;
        var token = new TowerDefenseSpawnToken("occurrence", "event", "site", 1, 1, "action");
        var (spawner, definition, members) = SpawnTrackedGroup(60, owned ? token : null);
        var oldGroup = members[0].GroupInstance;
        foreach (var npc in members)
        {
            npc.Hp = 0;
            spawner.DoDespawn(npc);
            spawner.Despawn(npc);
        }
        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));
        spawner.DoSpawn();
        await Assert.That(definition.Positions.Count).IsEqualTo(3);

        var replacement = owned ? spawner.ForceSpawnOwned(token with { Generation = 2 }) : spawner.ForceSpawn(0);

        await Assert.That(replacement).IsNotNull();
        await Assert.That(definition.Positions.Count).IsEqualTo(6);
        await Assert.That(oldGroup.IsRetired).IsTrue();
        await Assert.That(replacement.GroupInstance).IsNotSameReferenceAs(oldGroup);
        if (owned)
            await Assert.That(replacement.TowerDefenseSpawnToken.Generation).IsEqualTo(2);
    }

    [Test]
    public async Task ExplicitSpawn_DoesNotReplaceASuppressedGroupWithSurvivorsOrCorpses()
    {
        var token = new TowerDefenseSpawnToken("occurrence", "event", "site", 1, 1, "action");
        var (spawner, definition, members) = SpawnTrackedGroup(60, token);
        foreach (var npc in members)
        {
            npc.Hp = 0;
            spawner.DoDespawn(npc);
        }
        spawner.Despawn(members[1]);

        spawner.ForceSpawn(0);

        await Assert.That(definition.Positions.Count).IsEqualTo(3);
        await Assert.That(members[0].GroupInstance.IsRetired).IsFalse();
    }

    [Test]
    public async Task GroupSpawnEffect_AppliesLifetimeToAllMembersAndSuppressesTheirRespawn()
    {
        _data.GetNpcGroup(50).EnableRespawn = true;
        var (spawner, definition, members) = SpawnTrackedGroup();
        var effect = new ProbeSpawnEffect { LifeTime = 12 };

        effect.ApplySpawnedOccurrence(members[0], null, null);

        await Assert.That(effect.Scheduled.Count).IsEqualTo(members.Count);
        await Assert.That(members.All(npc => effect.Scheduled.Count(entry => ReferenceEquals(entry.Npc, npc)) == 1)).IsTrue();
        await Assert.That(effect.Scheduled.All(entry => entry.Delay == TimeSpan.FromSeconds(12))).IsTrue();
        await Assert.That(spawner.RespawnTime).IsEqualTo(0);
        // Another occurrence can change the spawner field. Explicit suppression belongs to this group.
        spawner.RespawnTime = 60;
        members[1].Hp = 0;
        spawner.DoDespawn(members[1]);
        spawner.Despawn(members[1]);
        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));
        await Assert.That(definition.Positions.Count).IsEqualTo(3);
        foreach (var (npc, _) in effect.Scheduled)
        {
            if (npc.Despawned)
                continue;
            new AAEmu.Game.Models.Tasks.World.NpcSpawnerDoDespawnTask(npc).Execute();
            spawner.Despawn(npc);
        }
        spawner.ProcessGroupRespawns(DateTime.UtcNow.AddHours(1));
        spawner.DoSpawn();
        await Assert.That(definition.Positions.Count).IsEqualTo(3);
        await Assert.That(spawner.SpawnedNpcs).IsEmpty();
    }

    [Test]
    public async Task OrdinarySpawnEffect_KeepsOneLifetimeAndZeroRespawn()
    {
        var npc = new ProbeNpc { Spawner = Spawner() };
        npc.Spawner.RespawnTime = 60;
        var effect = new ProbeSpawnEffect { LifeTime = 12 };

        effect.ApplySpawnedOccurrence(npc, null, null);

        await Assert.That(effect.Scheduled.Single().Npc).IsSameReferenceAs(npc);
        await Assert.That(effect.Scheduled.Single().Delay).IsEqualTo(TimeSpan.FromSeconds(12));
        await Assert.That(npc.Spawner.RespawnTime).IsEqualTo(0);
    }

    [Test]
    public async Task Formation_RotatesAuthoredOffsetsAndHoldsPositionWithoutALiveMoveLeader()
    {
        var definition = Definition();
        var spawner = Spawner();
        spawner.Position.Yaw = MathF.PI / 2;
        var members = definition.Spawn(spawner);
        var group = members[0].GroupInstance;
        var follower = members[1];

        await Assert.That(group.TryGetFormationPoint(follower, out var point, out var tension)).IsTrue();
        await Assert.That(System.Numerics.Vector3.Distance(point, new(103, 202, 30))).IsLessThan(0.001f);
        await Assert.That(tension).IsEqualTo(1f);
        await Assert.That(group.TryGetFormationPoint(members[0], out _, out _)).IsFalse();
        members[0].Hp = 0;
        follower.Transform.Local.SetPosition(130, 240, 31);

        await Assert.That(group.TryGetFormationPoint(follower, out point, out _)).IsTrue();
        await Assert.That(point).IsEqualTo(follower.Transform.World.Position);
    }

    [Test]
    public async Task MissingMemberTemplate_PublishesNothingAndDiscardsPreparedMembers()
    {
        var definition = Definition();
        definition.FailPreparation = 3;

        await Assert.That(() => definition.Spawn(Spawner())).Throws<InvalidDataException>();

        await Assert.That(definition.Published).IsEmpty();
        await Assert.That(definition.Discarded.Count).IsEqualTo(2);
        await Assert.That(definition.Discarded.All(npc => npc.GroupInstance == null)).IsTrue();
    }

    [Test]
    public async Task RejectedSpawnEvent_RemovesTheWholePreparedOccurrence()
    {
        var definition = Definition();
        definition.FailCompletion = 2;

        await Assert.That(() => definition.Spawn(Spawner())).Throws<InvalidOperationException>();

        await Assert.That(definition.Published.Count).IsEqualTo(3);
        await Assert.That(definition.Discarded.Count).IsEqualTo(3);
        await Assert.That(definition.Discarded.All(npc => npc.GroupInstance == null)).IsTrue();
    }

    [Test]
    public async Task OrdinaryNpcWithCollidingId_RemainsOneOrdinaryNpc()
    {
        var definition = Definition();
        definition.MemberType = "Npc";

        var spawned = definition.Spawn(Spawner());

        await Assert.That(spawned.Single().TemplateId).IsEqualTo(50u);
        await Assert.That(spawned.Single().GroupInstance).IsNull();
        await Assert.That(definition.Published.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownGroup_DoesNotCreateAnNpcWithTheGroupId()
    {
        var definition = Definition();
        definition.MemberId = 999;

        await Assert.That(definition.Spawn(Spawner())).IsEmpty();
        await Assert.That(definition.Positions).IsEmpty();
    }

    [Test]
    public async Task NpcSpawnerIndex_SeparatesGroupIdsFromNpcIds()
    {
        var data = new NpcGameData();
        var group = new NpcSpawnerNpc { Id = 1, MemberId = 50, MemberType = "NpcGroup", NpcSpawnerTemplateId = 9571 };
        var ordinary = new NpcSpawnerNpc { Id = 2, MemberId = 50, MemberType = "Npc", NpcSpawnerTemplateId = 100 };
        data.AddNpcSpawnerNpc(group);
        data.AddNpcSpawnerNpc(ordinary);
        data.PostLoad();
        data.AddMemberAndSpawnerTemplateIds(group);

        await Assert.That(data.GetSpawnerIds(50)).IsEquivalentTo(new uint[] { 100 });
        await Assert.That(data.GetSpawnerIds(8564)).IsNull();
    }

    [Test]
    public async Task Reload_ReplacesRemovedRowsWithoutChangingLiveOccurrenceTemplates()
    {
        var members = Definition().Spawn(Spawner());
        var originalTemplate = members[0].GroupInstance.Template;
        Execute("UPDATE npc_groups SET enable_respawn='t'; DELETE FROM npc_group_members WHERE id=193;");

        _data.Load(_connection);

        await Assert.That(_data.GetNpcGroupMembers(50).Count).IsEqualTo(2);
        await Assert.That(_data.GetNpcGroupMember(50, 193)).IsNull();
        await Assert.That(_data.GetNpcGroup(50).EnableRespawn).IsTrue();
        await Assert.That(originalTemplate.EnableRespawn).IsFalse();
        await Assert.That(members[0].GroupInstance.GetMembers().Length).IsEqualTo(3);
    }

    private static ProbeDefinition Definition() => new()
        { Id = 1, MemberId = 50, MemberType = "NpcGroup", NpcSpawnerTemplateId = 9571, Weight = 1 };

    private static NpcSpawner Spawner() => new()
        { Id = 1, SpawnerId = 9571, Position = new WorldSpawnPosition { X = 100, Y = 200, Z = 30 } };

    private (NpcSpawner Spawner, ProbeDefinition Definition, List<Npc> Members) SpawnTrackedGroup(int delay = 60,
        TowerDefenseSpawnToken token = null)
    {
        var definition = Definition();
        definition.EventToken = token;
        var spawner = Spawner();
        spawner.ParentWorld = _world;
        spawner.RespawnTime = delay;
        spawner.Template = new NpcSpawnerTemplate
        {
            Id = 9571, MaxPopulation = 1, ActivationState = true, TestRadiusNpc = 0,
            Npcs = [definition]
        };
        spawner.SpawnableNpcs = [definition];
        spawner.DoSpawn();
        return (spawner, definition, spawner.SpawnedNpcs[9571].ToList());
    }

    private void Replace<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, instance);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ProbeDefinition : NpcSpawnerNpc
    {
        public List<WorldSpawnPosition> Positions { get; } = [];
        public HashSet<Npc> Published { get; } = [];
        public List<Npc> Discarded { get; } = [];
        public List<int> CompleteSnapshots { get; } = [];
        public int FailPreparation { get; set; }
        public int FailCompletion { get; set; }
        public TowerDefenseSpawnToken EventToken { get; set; }

        protected override Npc PrepareNpc(NpcSpawner spawner, uint ownerId, uint templateId, WorldSpawnPosition position)
        {
            Positions.Add(position.Clone());
            if (Positions.Count == FailPreparation)
                return null;
            var npc = new ProbeNpc
            {
                ObjId = ObjectIdManager.Instance.GetNextId(), TemplateId = templateId, Spawner = spawner, OwnerId = ownerId,
                Hp = 100, ParentWorld = spawner.ParentWorld,
                TowerDefenseSpawnToken = spawner.PendingTowerDefenseSpawnToken ?? EventToken
            };
            npc.Transform.Local.SetPosition(position.AsPositionVector(), new(0, 0, position.Yaw));
            return npc;
        }

        protected override void PublishNpcObject(Npc npc) => Published.Add(npc);

        protected override bool CompleteNpcSpawn(Npc npc)
        {
            CompleteSnapshots.Add(npc.GroupInstance?.GetMembers().Count(Published.Contains) ?? 1);
            return CompleteSnapshots.Count != FailCompletion;
        }

        protected override void DiscardNpc(Npc npc) => Discarded.Add(npc);
    }

    private sealed class ProbeNpc : Npc
    {
        public override void Delete() => GroupInstance?.Detach(this);
    }

    private sealed class ProbeSpawnEffect : NpcSpawnerSpawnEffect
    {
        public List<(Npc Npc, TimeSpan Delay)> Scheduled { get; } = [];
        protected override void ScheduleDespawn(Npc npc, TimeSpan delay) => Scheduled.Add((npc, delay));
    }
}
