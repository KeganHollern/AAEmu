using AAEmu.Game.GameData;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class SlaveGameDataTests
{
    [Test]
    [Arguments("t", true)]
    [Arguments("f", false)]
    public async Task Load_TextMountability_PreservesAuthoredValue(string mountable, bool expected)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO slaves VALUES (
                106, 'Comet Speedster', 1323, @mountable, 0, 0, 143, 50, 50, 5,
                50, 0, 0, 'f', 5, 0, 0, 0);
            """;
        command.Parameters.AddWithValue("@mountable", mountable);
        command.ExecuteNonQuery();
        var data = new SlaveGameData();

        data.Load(connection);

        await Assert.That(data.GetSlaveTemplate(106).Mountable).IsEqualTo(expected);
    }

    [Test]
    public async Task ExactClient_LoadsAuthoredMountabilityForEveryVehicle()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var data = new SlaveGameData();
        data.Load(connection);

        await Assert.That(data.GetSlaveTemplate(106).Mountable).IsTrue();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, mountable FROM slaves";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var template = data.GetSlaveTemplate((uint)reader.GetInt64(0));
            await Assert.That(template.Mountable).IsEqualTo(reader.GetString(1) == "t");
        }
    }

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE slaves (
                id INTEGER, name TEXT, model_id INTEGER, mountable TEXT,
                spawn_x_offset REAL, spawn_y_offset REAL, faction_id INTEGER,
                level INTEGER, cost INTEGER, slave_kind_id INTEGER,
                spawn_valid_area_range INTEGER, slave_initial_item_pack_id INTEGER,
                slave_customizing_id INTEGER, customizable TEXT, portal_time REAL,
                hp25_doodad_count INTEGER, hp50_doodad_count INTEGER, hp75_doodad_count INTEGER);
            CREATE TABLE unit_modifiers (owner_type TEXT);
            CREATE TABLE slave_initial_items (id INTEGER);
            CREATE TABLE slave_initial_buffs (id INTEGER);
            CREATE TABLE slave_passive_buffs (id INTEGER);
            CREATE TABLE slave_doodad_bindings (id INTEGER);
            CREATE TABLE slave_healing_point_doodads (id INTEGER);
            CREATE TABLE slave_bindings (id INTEGER);
            CREATE TABLE slave_drop_doodads (id INTEGER);
            CREATE TABLE slave_mount_skills (id INTEGER);
            CREATE TABLE repairable_slaves (id INTEGER);
            """;
        command.ExecuteNonQuery();
        return connection;
    }
}
