using System.Buffers.Binary;
using System.Collections;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed partial class QuestCompletionPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reward_RealCommit_ReloadsCompletionAndAchievementsBeforeSuccessCallback(bool persistAcceptRow)
    {
        using var graph = new CompletionGraph(4_100_241);
        var quest = graph.AddRewardAttempt(persistAcceptRow);
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, args) =>
        {
            Assert.Equal(CompletionGraph.QuestId, args.QuestId);
            Assert.Empty(graph.Owner.Quests.ActiveQuests);
            Assert.True(graph.Owner.Quests.HasQuestCompleted(quest.TemplateId));
            graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
            callbacks++;
        };

        quest.GoToNextStep();
        quest.GoToNextStep();

        Assert.Equal(1, callbacks);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Equal(1, graph.CompletionPacketCount);
        Assert.True(graph.Owner.Achievements.IsCompleted(CompletionGraph.TypeAchievementId));
        Assert.Equal(1u, graph.Owner.Achievements.GetAmount(CompletionGraph.CategoryAchievementId));
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
    }

    [Fact]
    public void Reward_ActiveRowDeleteFails_RollsBackCompletionAndAchievementThenRetriesOnce()
    {
        using var graph = new CompletionGraph(4_100_240);
        var quest = graph.AddRewardAttempt();
        var before = quest.WriteData();
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        var trigger = $"quest_completion_delete_failure_{graph.Owner.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE DELETE ON quests FOR EACH ROW BEGIN IF OLD.owner={graph.Owner.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected quest completion deletion failure'; END IF; END");
        try
        {
            quest.GoToNextStep();

            graph.AssertStoredState(activeCount: 1, completed: false, typeCount: 0, categoryCount: 0);
            Assert.Same(quest, graph.Owner.Quests.ActiveQuests[quest.TemplateId]);
            Assert.Equal(QuestStatus.Ready, quest.Status);
            Assert.Equal(QuestComponentKind.Reward, quest.Step);
            Assert.Equal(before, graph.ReadStoredQuest().WriteData());
            Assert.Equal(0u, graph.Owner.Achievements.GetAmount(CompletionGraph.TypeAchievementId));
            Assert.Equal(0u, graph.Owner.Achievements.GetAmount(CompletionGraph.CategoryAchievementId));
            Assert.Equal(0, callbacks);
            Assert.Equal(0, graph.CompletionPacketCount);
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}");
        }

        quest.GoToNextStep();
        quest.GoToNextStep();

        Assert.Empty(graph.Owner.Quests.ActiveQuests);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, graph.CompletionPacketCount);
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
    }

    [Theory]
    [InlineData("completed_quests", "NEW.owner")]
    [InlineData("character_achievement_records", "NEW.character_id")]
    [InlineData("character_achievements", "NEW.character_id")]
    public void Reward_RealInsertFails_PreservesAttemptAndRetriesWithoutDuplicateProgress(string table, string ownerField)
    {
        using var graph = new CompletionGraph(4_100_242);
        var quest = graph.AddRewardAttempt();
        var before = quest.WriteData();
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        var trigger = $"quest_completion_insert_failure_{graph.Owner.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW BEGIN IF {ownerField}={graph.Owner.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected quest completion insertion failure'; END IF; END");
        try
        {
            quest.GoToNextStep();
            quest.GoToNextStep();

            Assert.Same(quest, graph.Owner.Quests.ActiveQuests[quest.TemplateId]);
            Assert.Equal(QuestStatus.Ready, quest.Status);
            Assert.Equal(QuestComponentKind.Reward, quest.Step);
            Assert.Equal(before, graph.ReadStoredQuest().WriteData());
            Assert.False(graph.Owner.Quests.HasQuestCompleted(quest.TemplateId));
            Assert.False(graph.Owner.Achievements.IsCompleted(CompletionGraph.TypeAchievementId));
            Assert.Equal(0u, graph.Owner.Achievements.GetAmount(CompletionGraph.TypeAchievementId));
            Assert.Equal(0u, graph.Owner.Achievements.GetAmount(CompletionGraph.CategoryAchievementId));
            Assert.Equal(0, callbacks);
            graph.AssertStoredState(activeCount: 1, completed: false, typeCount: 0, categoryCount: 0);
            Assert.Equal(0, graph.CompletionPacketCount);
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}");
        }

        quest.GoToNextStep();
        quest.GoToNextStep();

        Assert.Empty(graph.Owner.Quests.ActiveQuests);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, graph.CompletionPacketCount);
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);
    }

    [Fact]
    public void Reward_RepeatableAttempts_CountEachAttemptOnceAndCategoryOnlyOnce()
    {
        using var graph = new CompletionGraph(4_100_243, repeatable: true);
        var first = graph.AddRewardAttempt();
        var callbacks = 0;
        graph.Owner.Events.OnQuestComplete += (_, _) => callbacks++;
        first.GoToNextStep();
        first.GoToNextStep();
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 1, categoryCount: 1);

        var second = graph.AddRewardAttempt();
        Assert.NotSame(first, second);
        first.GoToNextStep();
        Assert.Same(second, graph.Owner.Quests.ActiveQuests[second.TemplateId]);
        Assert.Equal(QuestStatus.Ready, second.Status);
        graph.AssertStoredState(activeCount: 1, completed: true, typeCount: 1, categoryCount: 1);

        second.GoToNextStep();
        second.GoToNextStep();
        first.GoToNextStep();

        Assert.Equal(2, callbacks);
        Assert.Equal(2, graph.CompletionPacketCount);
        Assert.Empty(graph.Owner.Quests.ActiveQuests);
        graph.AssertStoredState(activeCount: 0, completed: true, typeCount: 2, categoryCount: 1);
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class CompletionGraph : IDisposable
    {
        internal const uint QuestId = 101;
        internal const uint TypeAchievementId = 910;
        internal const uint CategoryAchievementId = 911;
        private const uint CategoryId = 45;
        private readonly QuestManager _manager = new(Mock.Of<ITaskManager>(), Mock.Of<IZoneManager>());
        private readonly QuestIdManager _ids = new();
        private readonly AchievementGameData _data = new();
        private readonly FieldInfo _managerField;
        private readonly object _previousManager;
        private readonly FieldInfo _permissionField;
        private readonly object _previousPermissionManager;
        private readonly FieldInfo _idManagerField;
        private readonly object _previousIdManager;
        private readonly FieldInfo _saveManagerField;
        private readonly object _previousSaveManager;
        private readonly QuestTemplate _template;
        private readonly List<Quest> _attempts = [];
        private readonly List<ushort> _packets = [];
        private readonly List<(FieldInfo Field, object Previous)> _reloadDependencies = [];

        internal Character Owner { get; }
        internal SaveManager Save { get; }
        internal int StopCalls { get; private set; }
        internal int CompletionPacketCount => _packets.Count(type => type == SCOffsets.SCQuestContextCompletedPacket);

        internal CompletionGraph(uint ownerId, bool repeatable = false)
        {
            _template = new QuestTemplate { Id = QuestId, CategoryId = CategoryId, Repeatable = repeatable };
            var reward = new QuestComponentTemplate(_template) { Id = 1015, KindId = QuestComponentKind.Reward };
            _template.Components.Add(reward.Id, reward);
            SetPrivate(_manager, "_questTemplates", new Dictionary<uint, QuestTemplate> { [QuestId] = _template });
            _managerField = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _previousManager = _managerField.GetValue(null);
            _managerField.SetValue(null, _manager);
            _permissionField = typeof(Singleton<PermissionManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _previousPermissionManager = _permissionField.GetValue(null);
            _permissionField.SetValue(null, new PermissionManager(Mock.Of<IAccountManager>()));
            Assert.True(_ids.Initialize(true));
            _idManagerField = typeof(QuestIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _previousIdManager = _idManagerField.GetValue(null);
            _idManagerField.SetValue(null, _ids);
            Save = new SaveManager(Mock.Of<ITaskManager>(), Mock.Of<IHousingManager>(), Mock.Of<IMailManager>(),
                Mock.Of<IItemManager>(), Mock.Of<IAuctionManager>(), Mock.Of<ICrimeManager>(),
                Mock.Of<IWorldManager>(), Mock.Of<IZoneManager>())
            {
                StopForConsistencyFailure = (_, _) => StopCalls++
            };
            _saveManagerField = typeof(Singleton<SaveManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _previousSaveManager = _saveManagerField.GetValue(null);
            _saveManagerField.SetValue(null, Save);
            InstallReloadDependency(new TaskManager(Mock.Of<ITickManager>()));
            InstallReloadDependency(new SkillManager(Mock.Of<IAnimationManager>(), Mock.Of<IPlotManager>()));
            InstallReloadDependency(new ExpressTextManager());
            InstallReloadDependency(new WorldManager(Mock.Of<ITickManager>(), Mock.Of<IWorldIdManager>(),
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>()),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>()),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>())));
            SetPrivate(_data, "_charRecords", new Dictionary<uint, CharRecords>
            {
                [TypeAchievementId] = new() { Id = TypeAchievementId, KindId = CharRecordKind.CompleteQuestType, Value1 = QuestId },
                [CategoryAchievementId] = new() { Id = CategoryAchievementId, KindId = CharRecordKind.CompleteQuestCategory, Value1 = CategoryId }
            });
            SetPrivate(_data, "_achievements", new Dictionary<uint, Achievements>
            {
                [TypeAchievementId] = new() { Id = TypeAchievementId, CompleteNum = 1, IsActive = true },
                [CategoryAchievementId] = new() { Id = CategoryAchievementId, CompleteNum = 100, IsActive = true }
            });
            SetPrivate(_data, "_achievementObjectives", new Dictionary<uint, List<AchievementObjectives>>
            {
                [TypeAchievementId] = [new() { Id = TypeAchievementId, AchievementId = TypeAchievementId, RecordId = TypeAchievementId }],
                [CategoryAchievementId] = [new() { Id = CategoryAchievementId, AchievementId = CategoryAchievementId, RecordId = CategoryAchievementId }]
            });
            _data.PostLoad();
            Owner = CreateOwner(ownerId, capturePackets: true);
            Execute($"INSERT INTO completed_quests(id,data,owner) VALUES(1,X'0100000000000000',{ownerId})");
            using var connection = MySQL.CreateConnection();
            Owner.Quests.Load(connection);
            Owner.Achievements.Load(connection);
        }

        internal Quest AddRewardAttempt(bool persist = true)
        {
            var quest = CreateQuest(Owner);
            quest.Id = _ids.GetNextId();
            Owner.Quests.ActiveQuests.Add(QuestId, quest);
            _attempts.Add(quest);
            if (!persist)
                return quest;
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO quests(id,template_id,data,status,owner) VALUES(@id,@template,@data,@status,@owner)";
            command.Parameters.AddWithValue("@id", quest.Id);
            command.Parameters.AddWithValue("@template", quest.TemplateId);
            command.Parameters.AddWithValue("@data", quest.WriteData());
            command.Parameters.AddWithValue("@status", (byte)quest.Status);
            command.Parameters.AddWithValue("@owner", Owner.Id);
            command.ExecuteNonQuery();
            return quest;
        }

        internal Quest ReadStoredQuest()
        {
            var quest = CreateQuest(CreateOwner(Owner.Id));
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,data,status FROM quests WHERE owner=@owner AND template_id=@template";
            command.Parameters.AddWithValue("@owner", Owner.Id);
            command.Parameters.AddWithValue("@template", QuestId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            quest.Id = reader.GetUInt32("id");
            quest.Status = (QuestStatus)reader.GetByte("status");
            quest.ReadData((byte[])reader["data"]);
            Assert.False(reader.Read());
            return quest;
        }

        internal void AssertStoredState(int activeCount, bool completed, uint typeCount, uint categoryCount)
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("@owner", Owner.Id);
            command.CommandText = "SELECT COUNT(*) FROM quests WHERE owner=@owner AND template_id=101";
            Assert.Equal(activeCount, Convert.ToInt32(command.ExecuteScalar()));
            command.CommandText = "SELECT data FROM completed_quests WHERE owner=@owner AND id=1";
            var flags = new BitArray((byte[])command.ExecuteScalar());
            Assert.True(flags[0]);
            Assert.Equal(completed, flags[(int)(QuestId % 64)]);
            command.CommandText = "SELECT COALESCE(MAX(amount),0) FROM character_achievement_records WHERE character_id=@owner AND record_id=910";
            Assert.Equal(typeCount, Convert.ToUInt32(command.ExecuteScalar()));
            command.CommandText = "SELECT COALESCE(MAX(amount),0) FROM character_achievement_records WHERE character_id=@owner AND record_id=911";
            Assert.Equal(categoryCount, Convert.ToUInt32(command.ExecuteScalar()));
            command.CommandText = "SELECT COUNT(*) FROM character_achievements WHERE character_id=@owner AND achievement_id=910";
            Assert.Equal(typeCount > 0 ? 1 : 0, Convert.ToInt32(command.ExecuteScalar()));

            var restored = CreateOwner(Owner.Id);
            restored.Quests.Load(connection);
            restored.Achievements.Load(connection);
            Assert.Equal(completed, restored.Quests.HasQuestCompleted(QuestId));
            Assert.True(restored.Quests.HasQuestCompleted(64));
            Assert.Equal(activeCount, restored.Quests.ActiveQuests.Count);
            Assert.Equal(typeCount > 0, restored.Achievements.IsCompleted(TypeAchievementId));
        }

        private Character CreateOwner(uint ownerId, bool capturePackets = false)
        {
            var owner = new Character(null) { Id = ownerId, Name = "quest-completion-tester" };
            owner.Quests = new CharacterQuests(owner, new GameScheduleManager(null, TimeProvider.System));
            var achievements = new CharacterAchievements(owner, _data, TimeProvider.System, null,
                unitRequirementsData: new UnitRequirementsGameData());
            typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, achievements);
            var session = new Mock<ISession>();
            owner.Connection = new GameConnection(session.Object) { ActiveChar = owner };
            if (capturePackets)
            {
                session.Setup(value => value.SendPacket(It.IsAny<byte[]>())).Callback<byte[]>(bytes =>
                    _packets.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6))));
            }
            return owner;
        }

        private Quest CreateQuest(Character owner)
        {
            return new Quest(_template, owner, _manager, Mock.Of<ITaskManager>(), Mock.Of<ISkillManager>(),
                Mock.Of<IExpressTextManager>(), Mock.Of<IWorldManager>())
            {
                Status = QuestStatus.Ready,
                Step = QuestComponentKind.Reward,
                Objectives = [1, 2, 3, 4, 5]
            };
        }

        private static void SetPrivate(object owner, string name, object value)
        {
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
        }

        private void InstallReloadDependency<T>(T value) where T : class
        {
            var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _reloadDependencies.Add((field, field.GetValue(null)));
            field.SetValue(null, value);
        }

        public void Dispose()
        {
            foreach (var quest in _attempts)
                quest.FinalizeQuestActs();
            Execute($"DELETE FROM quests WHERE owner={Owner.Id}; DELETE FROM completed_quests WHERE owner={Owner.Id}; DELETE FROM character_achievement_records WHERE character_id={Owner.Id}; DELETE FROM character_achievements WHERE character_id={Owner.Id}");
            _managerField.SetValue(null, _previousManager);
            _permissionField.SetValue(null, _previousPermissionManager);
            _idManagerField.SetValue(null, _previousIdManager);
            _saveManagerField.SetValue(null, _previousSaveManager);
            foreach (var (field, previous) in _reloadDependencies)
                field.SetValue(null, previous);
        }
    }
}
