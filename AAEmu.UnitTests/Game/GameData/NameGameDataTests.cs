using AAEmu.Game.GameData;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class NameGameDataTests
{
    [Test]
    public async Task Load_NameAndChatRows_OnlyPublishesNameRules()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO allowed_name_chars VALUES (1, 'a', 1), (2, '가', 3);
            INSERT INTO blocked_texts VALUES
                (1, 'Admin', 5, 't', 'f', 't'),
                (2, 'GM', 2, 't', 'f', 'f'),
                (3, 'Chatword', 8, 'f', 't', 'f'),
                (4, 'Both', 4, 't', 't', 'f');
            """);
        var data = new NameGameData();

        data.Load(connection);
        data.PostLoad();

        await Assert.That(data.Data.AllowedCharacters.Count).IsEqualTo(2);
        await Assert.That(data.Data.AllowedCharacters.Contains('a')).IsTrue();
        await Assert.That(data.Data.AllowedCharacters.Contains('가')).IsTrue();
        await Assert.That(data.Data.BlockedNames.Length).IsEqualTo(3);
        await Assert.That(data.Data.BlockedNames.Contains(new NameGameData.BlockedName("Admin", true))).IsTrue();
        await Assert.That(data.Data.BlockedNames.Contains(new NameGameData.BlockedName("GM", false))).IsTrue();
        await Assert.That(data.Data.BlockedNames.Contains(new NameGameData.BlockedName("Both", false))).IsTrue();
    }

    [Test]
    [Arguments("", 0)]
    [Arguments("ab", 2)]
    [Arguments("가", 1)]
    [Arguments("a", -1)]
    [Arguments("\0", 1)]
    public async Task Load_InvalidAllowedCharacter_RejectsTheSnapshot(string text, int bytes)
    {
        using var connection = CreateConnection();
        InsertAllowed(connection, 1, text, bytes);
        var data = new NameGameData();

        await Assert.That(() => data.Load(connection)).Throws<InvalidDataException>();
        await Assert.That(data.Data.AllowedCharacters.Count).IsEqualTo(0);
        await Assert.That(data.Data.BlockedNames.IsEmpty).IsTrue();
    }

    [Test]
    [Arguments(1, "b")]
    [Arguments(2, "a")]
    public async Task Load_DuplicateAllowedIdOrCharacter_RejectsTheSnapshot(int id, string text)
    {
        using var connection = CreateConnection();
        InsertAllowed(connection, 1, "a", 1);
        InsertAllowed(connection, id, text, 1);

        await Assert.That(() => new NameGameData().Load(connection)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("", 0, "t")]
    [Arguments("Admin", 4, "t")]
    [Arguments("Admin", 5, "invalid")]
    [Arguments("Ad\0min", 6, "f")]
    public async Task Load_InvalidBlockedName_RejectsTheSnapshot(string text, int bytes, string partialMatch)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO blocked_texts VALUES (1, @text, @bytes, 't', 'f', @partial)";
        command.Parameters.AddWithValue("@text", text);
        command.Parameters.AddWithValue("@bytes", bytes);
        command.Parameters.AddWithValue("@partial", partialMatch);
        command.ExecuteNonQuery();

        await Assert.That(() => new NameGameData().Load(connection)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Load_DuplicateBlockedId_RejectsTheSnapshot()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO blocked_texts VALUES
                (1, 'Admin', 5, 't', 'f', 't'),
                (1, 'GM', 2, 't', 'f', 'f');
            """);

        await Assert.That(() => new NameGameData().Load(connection)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Load_ChangedData_ReplacesBothTablesTogether()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO allowed_name_chars VALUES (1, 'a', 1);
            INSERT INTO blocked_texts VALUES (1, 'Admin', 5, 't', 'f', 't');
            """);
        var data = new NameGameData();
        data.Load(connection);
        var previous = data.Data;

        Execute(connection, """
            DELETE FROM allowed_name_chars;
            DELETE FROM blocked_texts;
            INSERT INTO allowed_name_chars VALUES (2, '가', 3);
            INSERT INTO blocked_texts VALUES (2, 'GM', 2, 't', 'f', 'f');
            """);
        data.Load(connection);

        await Assert.That(data.Data.AllowedCharacters.Contains('a')).IsFalse();
        await Assert.That(data.Data.AllowedCharacters.Contains('가')).IsTrue();
        await Assert.That(data.Data.BlockedNames.Length).IsEqualTo(1);
        await Assert.That(data.Data.BlockedNames[0].Text).IsEqualTo("GM");
        await Assert.That(previous.AllowedCharacters.Contains('a')).IsTrue();
        await Assert.That(previous.BlockedNames[0].Text).IsEqualTo("Admin");
    }

    [Test]
    public async Task Load_InvalidSecondTable_KeepsBothPreviousTables()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO allowed_name_chars VALUES (1, 'a', 1);
            INSERT INTO blocked_texts VALUES (1, 'Admin', 5, 't', 'f', 't');
            """);
        var data = new NameGameData();
        data.Load(connection);
        var previous = data.Data;
        Execute(connection, """
            DELETE FROM allowed_name_chars;
            INSERT INTO allowed_name_chars VALUES (2, '가', 3);
            UPDATE blocked_texts SET bytes = 4;
            """);

        await Assert.That(() => data.Load(connection)).Throws<InvalidDataException>();
        await Assert.That(ReferenceEquals(data.Data, previous)).IsTrue();
    }

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE allowed_name_chars (id INTEGER, char TEXT, bytes INTEGER);
            CREATE TABLE blocked_texts
                (id INTEGER, utf8str TEXT, bytes INTEGER, check_name TEXT, check_chat TEXT, partial_match TEXT);
            """);
        return connection;
    }

    private static void InsertAllowed(SqliteConnection connection, int id, string text, int bytes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO allowed_name_chars VALUES (@id, @text, @bytes)";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@text", text);
        command.Parameters.AddWithValue("@bytes", bytes);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
