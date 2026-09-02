using AAEmu.Game.GameData;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public class ChatSpamGameDataTests
{
    [Test]
    public async Task TryMatch_ValidGraph_UsesLiteralOrdinalIgnoreCaseTraversal()
    {
        using var connection = CreateConnection();
        Execute(connection, """
                            INSERT INTO chat_spam_rules VALUES (1, 'valid');
                            INSERT INTO chat_spam_rule_details VALUES
                            (10, 1, 'DETECTED', 0, 0, 'f', 't'),
                            (11, 1, 'mmocpu', 10, 12, 't', 'f'),
                            (12, 1, 'wowgl.', 10, 13, 'f', 'f'),
                            (13, 1, 'gold%', 10, 0, 'f', 'f');
                            """);
        var data = new ChatSpamGameData();

        data.Load(connection);
        data.PostLoad();

        await Assert.That(data.TryMatch("visit MMOCPU today", out var firstMatch)).IsTrue();
        await Assert.That(firstMatch.RuleId).IsEqualTo(1u);
        await Assert.That(firstMatch.RuleName).IsEqualTo("valid");
        await Assert.That(firstMatch.DetailId).IsEqualTo(11u);
        await Assert.That(firstMatch.DetailText).IsEqualTo("mmocpu");

        await Assert.That(data.TryMatch("see WOWGL. now", out var punctuationMatch)).IsTrue();
        await Assert.That(punctuationMatch.DetailId).IsEqualTo(12u);
        await Assert.That(data.TryMatch("cheap GOLD% here", out var percentMatch)).IsTrue();
        await Assert.That(percentMatch.DetailId).IsEqualTo(13u);

        await Assert.That(data.TryMatch("wowglX and goldX are not literals", out _)).IsFalse();
        await Assert.That(data.TryMatch("ordinary chat", out _)).IsFalse();
    }

    [Test]
    public async Task PostLoad_MalformedGraphs_DisablesOnlyAffectedRules()
    {
        using var connection = CreateConnection();
        Execute(connection, """
                            INSERT INTO chat_spam_rules VALUES
                            (1, 'valid'),
                            (2, 'missing-start'),
                            (3, 'duplicate-end'),
                            (4, 'empty-text'),
                            (5, 'dangling-edge'),
                            (6, 'cross-rule-edge'),
                            (8, 'unreachable-end');

                            INSERT INTO chat_spam_rule_details VALUES
                            (10, 1, 'DETECTED', 0, 0, 'f', 't'),
                            (11, 1, 'valid-token', 10, 0, 't', 'f'),

                            (20, 2, 'DETECTED', 0, 0, 'f', 't'),
                            (21, 2, 'missing-start-token', 20, 0, 'f', 'f'),

                            (30, 3, 'DETECTED ONE', 0, 0, 'f', 't'),
                            (31, 3, 'DETECTED TWO', 0, 0, 'f', 't'),
                            (32, 3, 'duplicate-end-token', 30, 31, 't', 'f'),

                            (40, 4, 'DETECTED', 0, 0, 'f', 't'),
                            (41, 4, '', 40, 0, 't', 'f'),

                            (50, 5, 'DETECTED', 0, 0, 'f', 't'),
                            (51, 5, 'dangling-token', 999, 0, 't', 'f'),

                            (60, 6, 'DETECTED', 0, 0, 'f', 't'),
                            (61, 6, 'cross-rule-token', 10, 0, 't', 'f'),

                            (80, 8, 'DETECTED', 0, 0, 'f', 't'),
                            (81, 8, 'unreachable-token', 0, 0, 't', 'f'),

                            (90, 999, 'orphan-token', 0, 0, 't', 't');
                            """);
        var data = new ChatSpamGameData();

        data.Load(connection);
        data.PostLoad();

        await Assert.That(data.Get(1).IsValid).IsTrue();
        foreach (var ruleId in new uint[] { 2, 3, 4, 5, 6, 8 })
            await Assert.That(data.Get(ruleId).IsValid).IsFalse();

        await Assert.That(data.TryMatch("contains VALID-TOKEN", out var match)).IsTrue();
        await Assert.That(match.RuleId).IsEqualTo(1u);
        await Assert.That(data.TryMatch("dangling-token", out _)).IsFalse();
        await Assert.That(data.TryMatch("cross-rule-token", out _)).IsFalse();

        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 2 && issue.Message.Contains("exactly one start node"))).IsTrue();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 3 && issue.Message.Contains("exactly one end node"))).IsTrue();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 4 && issue.Message.Contains("empty nonterminal text"))).IsTrue();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 5 && issue.Message.Contains("dangling"))).IsTrue();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 6 && issue.Message.Contains("in chat_spam_rule 1"))).IsTrue();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 8 && issue.Message.Contains("cannot reach its end node"))).IsTrue();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 999 && issue.DetailId == 90)).IsTrue();
    }

    [Test]
    public async Task PostLoad_CyclicGraph_DisablesOnlyAffectedRule()
    {
        using var connection = CreateConnection();
        Execute(connection, """
                            INSERT INTO chat_spam_rules VALUES
                            (1, 'valid'),
                            (7, 'cycle');
                            INSERT INTO chat_spam_rule_details VALUES
                            (10, 1, 'DETECTED', 0, 0, 'f', 't'),
                            (11, 1, 'valid-token', 10, 0, 't', 'f'),

                            (70, 7, 'DETECTED', 0, 0, 'f', 't'),
                            (71, 7, 'cycle-a', 72, 70, 't', 'f'),
                            (72, 7, 'cycle-b', 71, 70, 'f', 'f');
                            """);
        var data = new ChatSpamGameData();

        data.Load(connection);
        data.PostLoad();

        await Assert.That(data.Get(1).IsValid).IsTrue();
        await Assert.That(data.Get(7).IsValid).IsFalse();
        await Assert.That(data.ValidationIssues.Any(issue =>
            issue.RuleId == 7 && issue.Message.Contains("cycle"))).IsTrue();
        await Assert.That(data.TryMatch("valid-token", out var match)).IsTrue();
        await Assert.That(match.RuleId).IsEqualTo(1u);
        await Assert.That(data.TryMatch("cycle-a cycle-b", out _)).IsFalse();
    }

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
                            CREATE TABLE chat_spam_rules (
                                id INTEGER, name TEXT);
                            CREATE TABLE chat_spam_rule_details (
                                id INTEGER, chat_spam_rule_id INTEGER, text TEXT,
                                detected_case_next_detail_id INTEGER,
                                not_detected_case_next_detail_id INTEGER,
                                start_node TEXT, end_node TEXT);
                            """);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
