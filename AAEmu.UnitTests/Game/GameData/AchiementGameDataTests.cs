using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement.Enums;

namespace AAEmu.UnitTests.Game.GameData;

/// <summary>
/// Tests for AchievementGameData class
/// </summary>
public class AchievementGameDataTests : SqliteTestBase
{
    [Test]
    public async Task CanCreateInstance()
    {
        var instance = new AchievementGameData();
        await Assert.That(instance).IsNotNull();
    }

    [Test]
    public async Task NewInstances_AreIndependent()
    {
        var instance1 = new AchievementGameData();
        var instance2 = new AchievementGameData();
        await Assert.That(instance2).IsNotSameReferenceAs(instance1);
    }

    [Test]
    public async Task Load_TextBooleans_PreservesActiveAndOrRules()
    {
        Execute("""
            INSERT INTO achievements
                (id, category_id, complete_num, complete_or, icon_id, is_active, is_hidden, item_id,
                 or_unit_reqs, parent_achievement_id, priority, sub_category_id)
            VALUES (1, 1, 2, 't', 0, 't', 'f', 0, 'f', 0, 1, 1);
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (10, 10, 0, 0);
            INSERT INTO achievement_objectives (id, achievement_id, or_unit_reqs, record_id)
            VALUES (100, 1, 't', 10);
            """);
        var gameData = new AchievementGameData();

        gameData.Load(Connection);
        gameData.PostLoad();

        await Assert.That(gameData.TryGetAchievement(1, out var achievement)).IsTrue();
        await Assert.That(achievement.IsActive).IsTrue();
        await Assert.That(achievement.IsHidden).IsFalse();
        await Assert.That(achievement.CompleteOr).IsTrue();
        await Assert.That(achievement.OrUnitReqs).IsFalse();
        await Assert.That(gameData.GetObjectives(1)[0].OrUnitReqs).IsTrue();
        await Assert.That(gameData.TryGetCharRecord(CharRecordKind.CharLevel, 0, 0, out var record)).IsTrue();
        await Assert.That(record.Id).IsEqualTo(10u);
    }

    [Test]
    public async Task GetMatchingCharRecords_Value2Wildcard_IsExplicitAndDoesNotDuplicateWildcardInput()
    {
        Execute("""
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (10, 29, 500, 3);
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (11, 29, 500, -1);
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (12, 29, 501, -1);
            """);
        var gameData = new AchievementGameData();
        gameData.Load(Connection);
        gameData.PostLoad();

        var exactOnly = gameData.GetMatchingCharRecords(CharRecordKind.GetItemType, 500, 3);
        var exactAndWildcard = gameData.GetMatchingCharRecords(CharRecordKind.GetItemType, 500, 3, true);
        var wildcardOnly = gameData.GetMatchingCharRecords(CharRecordKind.GetItemType, 500, 4, true);
        var wildcardInput = gameData.GetMatchingCharRecords(
            CharRecordKind.GetItemType,
            500,
            uint.MaxValue,
            true);

        await Assert.That(exactOnly.Select(record => record.Id)).IsEquivalentTo([10u]);
        await Assert.That(exactAndWildcard.Select(record => record.Id)).IsEquivalentTo([10u, 11u]);
        await Assert.That(wildcardOnly.Select(record => record.Id)).IsEquivalentTo([11u]);
        await Assert.That(wildcardInput.Select(record => record.Id)).IsEquivalentTo([11u]);
    }

    [Test]
    public async Task GetCharRecords_ReturnsAllValue2SelectorsInStableOrder()
    {
        Execute("""
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (10, 25, 500, 35);
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (11, 25, 500, 0);
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (12, 25, 500, 20);
            INSERT INTO char_records (id, kind_id, value1, value2) VALUES (13, 25, 501, 20);
            """);
        var gameData = new AchievementGameData();
        gameData.Load(Connection);
        gameData.PostLoad();

        var records = gameData.GetCharRecords(CharRecordKind.KillNpc, 500);

        await Assert.That(records.Select(record => record.Id)).IsEquivalentTo([11u, 12u, 10u]);
    }

    protected override void CreateTestSchema()
    {
        base.CreateTestSchema();
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

    private void Execute(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
