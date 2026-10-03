using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Music;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class MusicNoteGameDataTests
{
    [Test]
    [Arguments(0, 200)]
    [Arguments(1, 400)]
    [Arguments(2, 600)]
    [Arguments(3, 800)]
    [Arguments(4, 1000)]
    [Arguments(5, 1000)]
    [Arguments(6, 1000)]
    [Arguments(7, 1000)]
    [Arguments(8, 1000)]
    [Arguments(9, 1000)]
    [Arguments(10, 1000)]
    public async Task Load_R208022Step_AllowsItsLimitAndRejectsOneMoreCharacter(int step, int expectedLimit)
    {
        using var connection = CreateConnection();
        InsertR208022Rows(connection);
        var data = new MusicNoteGameData();

        data.Load(connection);
        data.PostLoad();

        await Assert.That(data.TryGetLimit((byte)step, out var limit)).IsTrue();
        await Assert.That(limit).IsEqualTo(expectedLimit);
        await Assert.That(MusicNoteRules.IsValid("Score", new string('c', expectedLimit), limit)).IsTrue();
        await Assert.That(MusicNoteRules.IsValid("Score", new string('c', expectedLimit + 1), limit)).IsFalse();
    }

    [Test]
    public async Task Load_R208022Rows_ExposesExactlyTheElevenAuthoredSteps()
    {
        using var connection = CreateConnection();
        InsertR208022Rows(connection);
        var data = new MusicNoteGameData();
        data.Load(connection);

        for (var step = 0; step <= byte.MaxValue; step++)
            await Assert.That(data.TryGetLimit((byte)step, out _)).IsEqualTo(step <= 10);
    }

    [Test]
    [Arguments(-1, 200)]
    [Arguments(256, 200)]
    [Arguments(1, 0)]
    [Arguments(1, -1)]
    public async Task Load_InvalidRow_RejectsTheDataWithoutPublishingPartialLimits(int step, int length)
    {
        using var connection = CreateConnection();
        Execute(connection, "INSERT INTO music_note_limits VALUES (1, 0, 200);");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO music_note_limits VALUES (2, @step, @length)";
        command.Parameters.AddWithValue("@step", step);
        command.Parameters.AddWithValue("@length", length);
        command.ExecuteNonQuery();
        var data = new MusicNoteGameData();

        await Assert.That(() => data.Load(connection)).Throws<InvalidDataException>();
        await Assert.That(data.TryGetLimit(0, out _)).IsFalse();
    }

    [Test]
    [Arguments(200)]
    [Arguments(400)]
    public async Task Load_DuplicateStep_RejectsBothIdenticalAndConflictingLimits(int duplicateLimit)
    {
        using var connection = CreateConnection();
        Execute(connection, "INSERT INTO music_note_limits VALUES (1, 0, 200);");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO music_note_limits VALUES (2, 0, @length)";
        command.Parameters.AddWithValue("@length", duplicateLimit);
        command.ExecuteNonQuery();

        await Assert.That(() => new MusicNoteGameData().Load(connection)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Load_NoNoviceRow_RejectsEmptyAndIncompleteData(bool hasOtherStep)
    {
        using var connection = CreateConnection();
        if (hasOtherStep)
            Execute(connection, "INSERT INTO music_note_limits VALUES (2, 1, 400);");

        await Assert.That(() => new MusicNoteGameData().Load(connection)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Load_ChangedRows_ReplacesLimitsAndRemovesDeletedSteps()
    {
        using var connection = CreateConnection();
        InsertR208022Rows(connection);
        var data = new MusicNoteGameData();
        data.Load(connection);

        Execute(connection, "DELETE FROM music_note_limits; INSERT INTO music_note_limits VALUES (1, 0, 300);");
        data.Load(connection);

        await Assert.That(data.TryGetLimit(0, out var limit)).IsTrue();
        await Assert.That(limit).IsEqualTo(300);
        await Assert.That(data.TryGetLimit(1, out _)).IsFalse();
        await Assert.That(data.TryGetLimit(10, out _)).IsFalse();
    }

    [Test]
    public async Task Load_InvalidReload_KeepsTheCompletePreviousSnapshot()
    {
        using var connection = CreateConnection();
        InsertR208022Rows(connection);
        var data = new MusicNoteGameData();
        data.Load(connection);

        Execute(connection, """
            DELETE FROM music_note_limits;
            INSERT INTO music_note_limits VALUES (1, 0, 300), (2, 1, 0);
            """);
        await Assert.That(() => data.Load(connection)).Throws<InvalidDataException>();

        await Assert.That(data.TryGetLimit(0, out var noviceLimit)).IsTrue();
        await Assert.That(noviceLimit).IsEqualTo(200);
        await Assert.That(data.TryGetLimit(1, out var nextLimit)).IsTrue();
        await Assert.That(nextLimit).IsEqualTo(400);
        await Assert.That(data.TryGetLimit(10, out var lastLimit)).IsTrue();
        await Assert.That(lastLimit).IsEqualTo(1000);
    }

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE music_note_limits (id INTEGER, step INTEGER, note_length INTEGER);");
        return connection;
    }

    private static void InsertR208022Rows(SqliteConnection connection)
    {
        // Exact rows shared by the r208022 client and server compacts. Deliberately not in step order.
        Execute(connection, """
            INSERT INTO music_note_limits VALUES
                (1, 0, 200), (10, 9, 1000), (11, 10, 1000),
                (2, 1, 400), (3, 2, 600), (4, 3, 800), (5, 4, 1000),
                (6, 5, 1000), (7, 6, 1000), (8, 7, 1000), (9, 8, 1000);
            """);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
