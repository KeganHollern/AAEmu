using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

using System.Net;
using System.Net.Sockets;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class CharacterAchievementsTests
{
    [Test]
    public async Task Reconcile_LevelFiftyFive_CompletesLeafChainsAndMetaAchievements()
    {
        using var data = new AchievementDataBuilder();
        AddLevelAchievements(data);
        var gameData = data.Build();
        var completedAt = new DateTimeOffset(2026, 8, 29, 15, 0, 0, TimeSpan.Zero);
        var character = new CharacterMock { Level = 55 };
        var achievements = new CharacterAchievements(
            character,
            gameData,
            new FakeTimeProvider(completedAt),
            () => true);

        achievements.ReconcileAuthoritativeState();

        uint[] expectedCompleted = [1, 2, 3, 4, 5, 6, 7, 1478, 1479, 1480, 1481, 1482, 1483];
        foreach (var achievementId in expectedCompleted)
        {
            await Assert.That(achievements.IsCompleted(achievementId)).IsTrue();
            await Assert.That(achievements.GetCompletionTime(achievementId)).IsEqualTo(completedAt.UtcDateTime);
        }

        await Assert.That(achievements.GetAmount(2)).IsEqualTo(5u);
        await Assert.That(achievements.GetAmount(7)).IsEqualTo(50u);
        await Assert.That(achievements.GetAmount(1483)).IsEqualTo(55u);
        await Assert.That(achievements.GetAmount(1)).IsEqualTo(6u);
        await Assert.That(achievements.GetAmount(1478)).IsEqualTo(5u);

        var packets = achievements.CreateSnapshotPackets();
        await Assert.That(packets.Count).IsEqualTo(1);
        var stream = packets[0].Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.ReadInt32()).IsEqualTo(expectedCompleted.Length);
        var expectedAmounts = new Dictionary<uint, uint>
        {
            [1] = 6,
            [2] = 5,
            [3] = 10,
            [4] = 20,
            [5] = 30,
            [6] = 40,
            [7] = 50,
            [1478] = 5,
            [1479] = 51,
            [1480] = 52,
            [1481] = 53,
            [1482] = 54,
            [1483] = 55
        };
        foreach (var achievementId in expectedCompleted)
        {
            await Assert.That(stream.ReadUInt32()).IsEqualTo(achievementId);
            await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedAmounts[achievementId]);
            await Assert.That(stream.ReadInt64()).IsEqualTo(Helpers.UnixTime(completedAt.UtcDateTime));
        }
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateMaximum_PersistenceFailure_HidesCompletionAndRetriesInPacketOrder()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false);
        data.AddObjective(1, 1000, 100);
        var completedAt = new DateTimeOffset(2026, 8, 29, 17, 0, 0, TimeSpan.Zero);
        var persistenceSucceeds = false;
        var session = new RecordingSession();
        var character = new CharacterMock { Connection = new GameConnection(session) };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            new FakeTimeProvider(completedAt),
            () => persistenceSucceeds);

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(session.Packets.Count).IsEqualTo(1);
        AssertPacketOpcode(session.Packets[0], SCOffsets.SCAchievementChangedPacket);
        await Assert.That(BitConverter.ToUInt32(session.Packets[0], 8)).IsEqualTo(1000u);
        await Assert.That(BitConverter.ToInt32(session.Packets[0], 12)).IsEqualTo(1);

        var pendingSnapshot = achievements.CreateSnapshotPackets().Single().Write(new PacketStream());
        pendingSnapshot.Rollback();
        await Assert.That(pendingSnapshot.ReadInt32()).IsEqualTo(1);
        await Assert.That(pendingSnapshot.ReadUInt32()).IsEqualTo(1000u);
        await Assert.That(pendingSnapshot.ReadUInt32()).IsEqualTo(1u);
        await Assert.That(pendingSnapshot.ReadInt64()).IsEqualTo(Helpers.UnixTime(DateTime.MinValue));

        persistenceSucceeds = true;
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(session.Packets.Count).IsEqualTo(2);
        AssertPacketOpcode(session.Packets[1], SCOffsets.SCAchievementCompletedPacket);
        await Assert.That(BitConverter.ToUInt32(session.Packets[1], 8)).IsEqualTo(1000u);
        await Assert.That(BitConverter.ToInt64(session.Packets[1], 12))
            .IsEqualTo(Helpers.UnixTime(completedAt.UtcDateTime));

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);
        await Assert.That(session.Packets.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SendSnapshot_PendingCompletion_RetriesPersistenceBeforeItShowsCompletion()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false);
        data.AddObjective(1, 1000, 100);
        var completedAt = new DateTimeOffset(2026, 8, 29, 17, 30, 0, TimeSpan.Zero);
        var persistenceSucceeds = false;
        var session = new RecordingSession();
        var character = new CharacterMock { Connection = new GameConnection(session) };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            new FakeTimeProvider(completedAt),
            () => persistenceSucceeds);
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);
        session.Packets.Clear();

        achievements.SendSnapshot();

        await Assert.That(session.Packets.Count).IsEqualTo(1);
        AssertPacketOpcode(session.Packets[0], SCOffsets.SCAchievementsPacket);
        await Assert.That(BitConverter.ToInt32(session.Packets[0], 8)).IsEqualTo(1);
        await Assert.That(BitConverter.ToUInt32(session.Packets[0], 12)).IsEqualTo(1000u);
        await Assert.That(BitConverter.ToUInt32(session.Packets[0], 16)).IsEqualTo(1u);
        await Assert.That(BitConverter.ToInt64(session.Packets[0], 20))
            .IsEqualTo(Helpers.UnixTime(DateTime.MinValue));

        persistenceSucceeds = true;
        session.Packets.Clear();
        achievements.SendSnapshot();

        await Assert.That(session.Packets.Count).IsEqualTo(1);
        AssertPacketOpcode(session.Packets[0], SCOffsets.SCAchievementsPacket);
        await Assert.That(BitConverter.ToInt64(session.Packets[0], 20))
            .IsEqualTo(Helpers.UnixTime(completedAt.UtcDateTime));
    }

    [Test]
    public async Task UpdateMaximum_LeafAndMetaCompletion_PreservesDependencyPacketOrder()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1483, 1, false);
        data.AddObjective(1, 1483, 100);
        data.AddRecord(101, CharRecordKind.CompleteAchievement, 1483);
        data.AddAchievement(1478, 1, false);
        data.AddObjective(2, 1478, 101);
        var session = new RecordingSession();
        var character = new CharacterMock { Connection = new GameConnection(session) };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 29, 18, 30, 0, TimeSpan.Zero)),
            () => true);

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(session.Packets.Count).IsEqualTo(4);
        ushort[] expectedOpcodes =
        [
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementCompletedPacket,
            SCOffsets.SCAchievementCompletedPacket
        ];
        uint[] expectedIds = [1483, 1478, 1483, 1478];
        for (var index = 0; index < session.Packets.Count; index++)
        {
            AssertPacketOpcode(session.Packets[index], expectedOpcodes[index]);
            await Assert.That(BitConverter.ToUInt32(session.Packets[index], 8)).IsEqualTo(expectedIds[index]);
        }
    }

    [Test]
    public async Task UpdateMaximum_ObjectiveRulesAndPrerequisite_CompleteInOrder()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddRecord(101, CharRecordKind.KillNpc, 2);
        data.AddRecord(102, CharRecordKind.KillNpc, 3);
        data.AddAchievement(1000, 5, false);
        data.AddObjective(1, 1000, 100);
        data.AddObjective(2, 1000, 101);
        data.AddAchievement(1001, 2, true);
        data.AddObjective(3, 1001, 100);
        data.AddObjective(4, 1001, 101);
        data.AddObjective(5, 1001, 102);
        data.AddAchievement(1002, 0, true);
        data.AddObjective(6, 1002, 100);
        data.AddObjective(7, 1002, 101);
        data.AddObjective(8, 1002, 102);
        data.AddAchievement(1003, 1, false);
        data.AddObjective(9, 1003, 102);
        data.AddPrerequisite(1, 1003, 1002);
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 2);
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 2, 0, 3);

        await Assert.That(achievements.IsCompleted(1000)).IsTrue();
        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(5u);
        await Assert.That(achievements.IsCompleted(1001)).IsTrue();
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(2u);
        await Assert.That(achievements.IsCompleted(1002)).IsFalse();
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(2u);
        await Assert.That(achievements.IsCompleted(1003)).IsFalse();

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 3, 0, 1);

        await Assert.That(achievements.IsCompleted(1002)).IsTrue();
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(3u);
        await Assert.That(achievements.IsCompleted(1003)).IsTrue();
        await Assert.That(achievements.GetAmount(1003)).IsEqualTo(1u);
    }

    [Test]
    public async Task ConfirmedSources_KeepSelectorsAndHourBoundariesIndependent()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(26, CharRecordKind.AbilityLevel, 1);
        data.AddRecord(27, CharRecordKind.AbilityLevel, 2);
        data.AddRecord(56, CharRecordKind.PlayTime);
        data.AddAchievement(9, 50, false);
        data.AddObjective(1, 9, 26);
        data.AddAchievement(10, 50, false);
        data.AddObjective(2, 10, 27);
        data.AddAchievement(31, 1, false);
        data.AddObjective(3, 31, 56);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 29, 16, 0, 0, TimeSpan.Zero));
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build(), timeProvider);

        achievements.UpdateAbilityLevel(AbilityType.Fight, 49);
        achievements.UpdatePlayTime(TimeSpan.FromMinutes(59));

        await Assert.That(achievements.GetAmount(9)).IsEqualTo(49u);
        await Assert.That(achievements.GetAmount(10)).IsEqualTo(0u);
        await Assert.That(achievements.GetAmount(31)).IsEqualTo(0u);

        achievements.UpdateAbilityLevel(AbilityType.Fight, 50);
        achievements.UpdatePlayTime(TimeSpan.FromHours(1));
        var abilityCompletion = achievements.GetCompletionTime(9);
        var playTimeCompletion = achievements.GetCompletionTime(31);

        await Assert.That(achievements.IsCompleted(9)).IsTrue();
        await Assert.That(achievements.IsCompleted(10)).IsFalse();
        await Assert.That(achievements.IsCompleted(31)).IsTrue();

        timeProvider.Advance(TimeSpan.FromDays(1));
        achievements.UpdateAbilityLevel(AbilityType.Fight, 55);
        achievements.UpdatePlayTime(TimeSpan.FromHours(2));

        await Assert.That(achievements.GetCompletionTime(9)).IsEqualTo(abilityCompletion);
        await Assert.That(achievements.GetCompletionTime(31)).IsEqualTo(playTimeCompletion);
    }

    [Test]
    public async Task CreateSnapshotPackets_FiftyOneEntries_UsesFiftyAndOneChunks()
    {
        using var data = new AchievementDataBuilder();
        for (uint index = 1; index <= 51; index++)
        {
            var achievementId = 2000 + index;
            data.AddRecord(index, CharRecordKind.KillNpc, index);
            data.AddAchievement(achievementId, 2, false);
            data.AddObjective(index, achievementId, index);
        }
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());
        for (uint index = 1; index <= 51; index++)
            achievements.UpdateMaximum(CharRecordKind.KillNpc, index, 0, 1);

        var packets = achievements.CreateSnapshotPackets();

        await Assert.That(packets.Count).IsEqualTo(2);
        var first = packets[0].Write(new PacketStream());
        first.Rollback();
        await Assert.That(first.ReadInt32()).IsEqualTo(50);
        for (uint index = 1; index <= 50; index++)
        {
            await Assert.That(first.ReadUInt32()).IsEqualTo(2000u + index);
            await Assert.That(first.ReadUInt32()).IsEqualTo(1u);
            await Assert.That(first.ReadInt64()).IsEqualTo(Helpers.UnixTime(DateTime.MinValue));
        }
        await Assert.That(first.LeftBytes).IsEqualTo(0);

        var second = packets[1].Write(new PacketStream());
        second.Rollback();
        await Assert.That(second.ReadInt32()).IsEqualTo(1);
        await Assert.That(second.ReadUInt32()).IsEqualTo(2051u);
        await Assert.That(second.ReadUInt32()).IsEqualTo(1u);
        await Assert.That(second.ReadInt64()).IsEqualTo(Helpers.UnixTime(DateTime.MinValue));
        await Assert.That(second.LeftBytes).IsEqualTo(0);
    }

    private static void AddLevelAchievements(AchievementDataBuilder data)
    {
        data.AddRecord(9, CharRecordKind.CharLevel);

        data.AddAchievement(1, 0, true);
        uint objectiveId = 1;
        uint completionRecordId = 10;
        for (uint achievementId = 2; achievementId <= 7; achievementId++)
        {
            data.AddAchievement(achievementId, achievementId switch
            {
                2 => 5,
                3 => 10,
                4 => 20,
                5 => 30,
                6 => 40,
                _ => 50
            }, false);
            data.AddObjective(objectiveId++, achievementId, 9);
            data.AddRecord(completionRecordId, CharRecordKind.CompleteAchievement, achievementId);
            data.AddObjective(objectiveId++, 1, completionRecordId++);
            if (achievementId > 2)
                data.AddPrerequisite(achievementId - 2, achievementId, achievementId - 1);
        }

        data.AddAchievement(1478, 0, true);
        for (uint achievementId = 1479; achievementId <= 1483; achievementId++)
        {
            data.AddAchievement(achievementId, achievementId - 1428, false);
            data.AddObjective(objectiveId++, achievementId, 9);
            data.AddRecord(completionRecordId, CharRecordKind.CompleteAchievement, achievementId);
            data.AddObjective(objectiveId++, 1478, completionRecordId++);
        }
    }

    private static void AssertPacketOpcode(byte[] packet, ushort opcode)
    {
        if (packet.Length < 8 || BitConverter.ToUInt16(packet, 6) != opcode)
            throw new InvalidOperationException($"Expected opcode 0x{opcode:X3}.");
    }

    internal sealed class AchievementDataBuilder : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public AchievementDataBuilder()
        {
            _connection.Open();
            Execute("""
                CREATE TABLE achievements (
                    id INTEGER PRIMARY KEY,
                    category_id INTEGER,
                    complete_num INTEGER,
                    complete_or NUM,
                    icon_id INTEGER,
                    is_active NUM,
                    is_hidden NUM,
                    item_id INTEGER,
                    or_unit_reqs NUM,
                    parent_achievement_id INTEGER,
                    priority INTEGER,
                    sub_category_id INTEGER
                );
                CREATE TABLE achievement_objectives (
                    id INTEGER PRIMARY KEY,
                    achievement_id INTEGER,
                    or_unit_reqs NUM,
                    record_id INTEGER
                );
                CREATE TABLE pre_completed_achievements (
                    id INTEGER PRIMARY KEY,
                    my_achievement_id INTEGER,
                    completed_achievement_id INTEGER
                );
                CREATE TABLE char_records (
                    id INTEGER PRIMARY KEY,
                    kind_id INTEGER,
                    value1 INTEGER,
                    value2 INTEGER
                );
                """);
        }

        public void AddAchievement(uint id, uint completeNum, bool completeOr)
        {
            Execute($"""
                INSERT INTO achievements
                    (id, category_id, complete_num, complete_or, icon_id, is_active, is_hidden, item_id,
                     or_unit_reqs, parent_achievement_id, priority, sub_category_id)
                VALUES ({id}, 1, {completeNum}, '{ToSqlBoolean(completeOr)}', 0, 't', 'f', 0, 'f', 0, {id}, 1);
                """);
        }

        public void AddObjective(uint id, uint achievementId, uint recordId)
        {
            Execute($"""
                INSERT INTO achievement_objectives (id, achievement_id, or_unit_reqs, record_id)
                VALUES ({id}, {achievementId}, 'f', {recordId});
                """);
        }

        public void AddRecord(uint id, CharRecordKind kind, uint value1 = 0, uint value2 = 0)
        {
            Execute($"INSERT INTO char_records (id, kind_id, value1, value2) VALUES ({id}, {(uint)kind}, {value1}, {value2});");
        }

        public void AddPrerequisite(uint id, uint achievementId, uint completedAchievementId)
        {
            Execute($"""
                INSERT INTO pre_completed_achievements (id, my_achievement_id, completed_achievement_id)
                VALUES ({id}, {achievementId}, {completedAchievementId});
                """);
        }

        public AchievementGameData Build()
        {
            var gameData = new AchievementGameData();
            gameData.Load(_connection);
            gameData.PostLoad();
            return gameData;
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        private void Execute(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static char ToSqlBoolean(bool value)
        {
            return value ? 't' : 'f';
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];

        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;

        public void SendPacket(byte[] packet)
        {
            Packets.Add(packet.ToArray());
        }

        public void AddAttribute(string name, object attribute)
        {
            _attributes.Add(name, attribute);
        }

        public object GetAttribute(string name)
        {
            return _attributes.GetValueOrDefault(name);
        }

        public void ClearAttribute(string name)
        {
            _attributes.Remove(name);
        }

        public void Close()
        {
        }
    }
}
