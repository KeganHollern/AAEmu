using AAEmu.Game.GameData;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class NpcAggroLinkGameDataTests
{
    [Test]
    public async Task Load_MultipleMemberships_UsesOnlySharedAuthoredLinks()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO aggro_links VALUES (109), (10), (27);
            INSERT INTO npcs VALUES (3690), (10095), (2325), (2326), (999);
            INSERT INTO npc_aggro_links VALUES
                (1, 3690, 109), (2, 10095, 109), (3, 10095, 10),
                (4, 2325, 27), (5, 2326, 27), (6, 3690, 109);
            """);
        var data = new NpcAggroLinkGameData();
        data.Load(connection);

        await Assert.That(data.AreLinked(3690, 10095)).IsTrue();
        await Assert.That(data.AreLinked(10095, 3690)).IsTrue();
        await Assert.That(data.AreLinked(2325, 2326)).IsTrue();
        await Assert.That(data.AreLinked(2325, 3690)).IsFalse();
        await Assert.That(data.AreLinked(3690, 999)).IsFalse();
        await Assert.That(data.AreLinked(0, 0)).IsFalse();
    }

    [Test]
    public async Task Load_MissingReferences_DoNotCreateLinks()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO aggro_links VALUES (109), (0);
            INSERT INTO npcs VALUES (3690), (10095), (0);
            INSERT INTO npc_aggro_links VALUES
                (1, 3690, 404), (2, 10095, 404), (3, 999, 109),
                (4, 3690, 109), (5, 0, 109), (6, 10095, 0);
            """);
        var data = new NpcAggroLinkGameData();
        data.Load(connection);

        await Assert.That(data.AreLinked(3690, 10095)).IsFalse();
        await Assert.That(data.AreLinked(3690, 999)).IsFalse();
        await Assert.That(data.AreLinked(3690, 0)).IsFalse();
    }

    [Test]
    public async Task Load_AfterMembershipRemoval_ReplacesPreviousSnapshot()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO aggro_links VALUES (109);
            INSERT INTO npcs VALUES (3690), (10095);
            INSERT INTO npc_aggro_links VALUES (1, 3690, 109), (2, 10095, 109);
            """);
        var data = new NpcAggroLinkGameData();
        data.Load(connection);
        await Assert.That(data.AreLinked(3690, 10095)).IsTrue();

        Execute(connection, "DELETE FROM npc_aggro_links WHERE npc_id = 10095;");
        data.Load(connection);
        await Assert.That(data.AreLinked(3690, 10095)).IsFalse();
    }

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE aggro_links (id INTEGER);
            CREATE TABLE npcs (id INTEGER);
            CREATE TABLE npc_aggro_links (id INTEGER, npc_id INTEGER, aggro_link_id INTEGER);
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
