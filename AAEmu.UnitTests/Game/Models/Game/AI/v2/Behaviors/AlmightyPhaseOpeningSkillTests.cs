using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Controls;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.AI.v2.Params.Almighty;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using Microsoft.Data.Sqlite;
using TUnit.Assertions.Enums;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Behaviors;

[NotInParallel]
public sealed class AlmightyPhaseOpeningSkillTests
{
    private const string TwoPhases = """
        aiPhaseChangeType = PHASE_TYPE_SEQUENCE,
        aiSkillLists = {
          { healthRange = {50,100}, startAiSkill = {skillType=23103, delay=3}, skills={{skillType=20350}} },
          { healthRange = {0,50}, startAiSkill = {skillType=22268, delay=3}, skills={{skillType=20350}} }
        }
        """;
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private WorldConfig _previousWorldConfig;
    private readonly Dictionary<uint, SkillTemplate> _skills = [];

    private void Replace<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous[field] = field.GetValue(null);
        field.SetValue(null, value);
    }

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        var buffs = new BuffGameData();
        var modifiers = typeof(BuffGameData).GetField("_buffModifiers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        modifiers.SetValue(buffs, Activator.CreateInstance(modifiers.FieldType));
        Replace(buffs);
        var skills = new SkillManager(null, null);
        // Exercise the ordinary skill dispatch and its authored delay without applying damage in this queue test.
        uint[] skillIds = [23103, 22268, 20350];
        foreach (var id in skillIds)
            _skills[id] = new SkillTemplate { Id = id, MaxRange = 200, SourceAlive = false };
        Field("_skills").SetValue(skills, _skills);
        const uint returning = (uint)BuffConstants.NpcReturn;
        Field("_buffs").SetValue(skills, new Dictionary<uint, BuffTemplate> { [returning] = new() { Id = returning } });
        foreach (var name in new[] { "_buffTags", "_taggedBuffs", "_skillModifiers", "_buffTriggers", "_combatBuffs" })
        {
            var field = Field(name);
            field.SetValue(skills, Activator.CreateInstance(field.FieldType));
        }

        static FieldInfo Field(string name) => typeof(SkillManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Replace(skills);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, original) in _previous)
            field.SetValue(null, original);
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task EachSelectedPhase_QueuesItsOpeningSkillOnceAndKeepsTheOrdinarySkills()
    {
        var ai = Start(TwoPhases);
        await Assert.That(ai.Attack.NextQueue(75)).IsEquivalentTo(new uint[] { 23103, 20350 }, CollectionOrdering.Matching);
        await Assert.That(ai.Attack.NextQueue(74)).IsEquivalentTo(new uint[] { 20350 });
        ai.GoToFollowPath();
        ai.GoToCombat();
        await Assert.That(ai.Attack.NextQueue(74)).IsEquivalentTo(new uint[] { 20350 });
        await Assert.That(ai.Attack.NextQueue(30)).IsEquivalentTo(new uint[] { 22268, 20350 }, CollectionOrdering.Matching);
        await Assert.That(ai.Attack.NextQueue(29)).IsEquivalentTo(new uint[] { 20350 });
        await Assert.That(ai.Attack.NextQueue(75)).IsEquivalentTo(new uint[] { 20350 });
    }

    [Test]
    public async Task CooldownWaitsAndANewCombatCanUseTheSameOpeningSkill()
    {
        var ai = Start(TwoPhases);
        ai.Owner.Cooldowns.AddCooldown(22268, 60000);
        await Assert.That(ai.Attack.NextQueue(15)).IsEquivalentTo(new uint[] { 20350 });
        ai.Owner.Cooldowns.RemoveCooldown(22268);
        await Assert.That(ai.Attack.NextQueue(15)).IsEquivalentTo(new uint[] { 22268, 20350 }, CollectionOrdering.Matching);
        ai.GoToReturn();
        ai.GoToCombat();
        await Assert.That(ai.Attack.NextQueue(15)).IsEquivalentTo(new uint[] { 22268, 20350 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CombatTick_DequeuesEachPhaseOpenerBeforeItsRegularAttack()
    {
        var ai = Start(TwoPhases);
        var world = new WorldInstance(new WorldTemplate { Id = 1, CellX = 1, CellY = 1 }, 0, true, 1);
        var target = new QuietNpc { ObjId = 2, Hp = 100, Template = new NpcTemplate() };
        var worlds = new WorldManager(null, null, null, null, null);
        var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worlds)!;
        instances[world.Id] = world;
        Replace(worlds);
        target.ParentWorld = ai.Owner.ParentWorld = world;
        world.AddObject(target);
        ai.Owner.AggroTable[target.ObjId] = new Aggro(target);
        ai.Owner.DisplacedUntil = DateTime.UtcNow.AddMinutes(1);
        ai.Owner.Hp = 75;
        ai.Attack.Tick(TimeSpan.FromMilliseconds(160));
        await Assert.That(ai.Attack.Pending).IsEquivalentTo(new uint[] { 20350 });
        await Assert.That(ai.Attack.DispatchedDelay).IsEqualTo(3f);

        await Task.Delay(170);
        ai.Attack.Tick(TimeSpan.FromMilliseconds(170));
        await Assert.That(ai.Attack.Pending).IsEmpty();
        await Assert.That(ai.Attack.DispatchedDelay).IsEqualTo(0f);
        ai.Owner.Hp = 30;
        await Task.Delay(170);
        ai.Attack.Tick(TimeSpan.FromMilliseconds(170));
        await Assert.That(ai.Attack.Pending).IsEquivalentTo(new uint[] { 20350 });
        await Assert.That(ai.Attack.DispatchedDelay).IsEqualTo(3f);
    }

    [Test]
    public async Task StockDragon_SelectsTheFiveOpeningSkillsInTheAuthoredHealthBands()
    {
        var ai = ReadStockNpc(12411, 3418);
        await Assert.That(ai.Owner.Level).IsEqualTo((byte)50);
        uint[] openings = [23103, 23453, 22269, 22268, 23454];
        var selected = new List<uint>();
        foreach (var hp in new[] { 100, 80, 65, 50, 30, 15 })
        {
            var first = ai.Attack.NextQueue(hp);
            selected.AddRange(first.Where(openings.Contains));
            var second = ai.Attack.NextQueue(hp);
            await Assert.That(second).IsNotEmpty();
            await Assert.That(second.Any(openings.Contains)).IsFalse();
        }
        await Assert.That(selected).IsEquivalentTo(openings, CollectionOrdering.Matching);
    }

    [Test]
    public async Task StockLevel50Guard_KeepsItsOrdinaryAttacksAtBothDragonThresholds()
    {
        var guard = ReadStockNpc(1067, 1189);
        await Assert.That(guard.Owner.Level).IsEqualTo((byte)50);
        foreach (var hp in new[] { 80, 30 })
        {
            var queue = guard.Attack.NextQueue(hp);
            // Both stock sets end in the same two attacks. The pull needs 10 m.
            await Assert.That(queue.TakeLast(2)).IsEquivalentTo(new uint[] { 17545, 17544 }, CollectionOrdering.Matching);
            await Assert.That(queue.Contains(20276u)).IsFalse();
        }
    }

    private DragonAi ReadStockNpc(int npcId, int profileId)
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 server compact.");
        using var compact = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        compact.Open();
        using var command = compact.CreateCommand();
        command.CommandText = """
            SELECT p.ai_param, n.level FROM npcs n JOIN npc_ai_params p ON p.id=n.npc_ai_param_id
            WHERE n.id=$npc AND n.ai_file_id=27 AND p.id=$profile
            """;
        command.Parameters.AddWithValue("$npc", npcId);
        command.Parameters.AddWithValue("$profile", profileId);
        AlmightyNpcAiParams profile;
        byte level;
        using (var npc = command.ExecuteReader())
        {
            if (!npc.Read())
                throw new InvalidDataException($"Missing stock Almighty NPC {npcId} with profile {profileId}.");
            profile = new AlmightyNpcAiParams(npc.GetString(0));
            level = npc.GetByte(1);
        }
        var ids = profile.AiSkillLists.SelectMany(phase => phase.StartAiSkills.Concat(
            phase.SkillLists.SelectMany(list => list.Skills))).Select(skill => skill.SkillId).Distinct();
        command.Parameters.Clear();
        command.CommandText = """
            SELECT id, min_range, max_range, target_type_id FROM skills WHERE id IN (SELECT value FROM json_each($ids))
            """;
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(ids));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = (uint)reader.GetInt32(0);
            _skills[id] = new SkillTemplate
            {
                Id = id, MinRange = reader.GetInt32(1), MaxRange = reader.GetInt32(2),
                TargetType = (SkillTargetType)reader.GetByte(3)
            };
        }
        var ai = Start(profile);
        ai.Owner.Level = level;
        return ai;
    }

    [Test]
    public async Task TwoDragonsWithTheSameTemplate_EachQueueTheirOwnPhaseOpener()
    {
        var profile = new AlmightyNpcAiParams(TwoPhases);
        var first = Start(profile);
        var second = Start(profile);
        await Assert.That(first.Attack.NextQueue(30)).IsEquivalentTo(new uint[] { 22268, 20350 }, CollectionOrdering.Matching);
        await Assert.That(second.Attack.NextQueue(30)).IsEquivalentTo(new uint[] { 22268, 20350 }, CollectionOrdering.Matching);
    }

    private static DragonAi Start(string lua) => Start(new AlmightyNpcAiParams(lua));

    private static DragonAi Start(AlmightyNpcAiParams profile)
    {
        var npc = new QuietNpc { ObjId = 1, Hp = 100, Template = new NpcTemplate { AiParams = profile } };
        var ai = new DragonAi { Owner = npc };
        npc.Ai = ai;
        ai.Start();
        ai.PathHandler.AiPathPointsRemaining.Enqueue(new AiPathPoint());
        ai.GoToCombat();
        return ai;
    }

    private sealed class DragonAi : NpcAi
    {
        public QueueProbe Attack { get; } = new();
        protected override void Build()
        {
            AddBehavior(BehaviorKind.AlmightyAttack, Attack);
            AddBehavior(BehaviorKind.FollowPath, new FollowPathBehavior());
            AddBehavior(BehaviorKind.ReturnState, new ReturnStateBehavior());
        }
        public override void GoToCombat() => SetCurrentBehavior(BehaviorKind.AlmightyAttack);
    }

    private sealed class QueueProbe : AlmightyAttackBehavior
    {
        internal float DispatchedDelay => _nextTimeToDelay;
        internal uint[] Pending => _skillQueue.Select(skill => skill.SkillId).ToArray();

        internal uint[] NextQueue(int hp)
        {
            Ai.Owner.Hp = hp;
            var profile = (AlmightyNpcAiParams)Ai.Param;
            RefreshSkillQueue(profile.AiSkillLists, profile);
            var ids = Pending;
            _skillQueue.Clear();
            return ids;
        }
    }

    private sealed class QuietNpc : Npc
    {
        public override int MaxHp => 100;
        public override bool UnitIsVisible(BaseUnit other) => other != null;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }
}
