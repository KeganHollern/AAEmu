using System.Numerics;

using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.World.Transform;

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
    [Arguments("VehicleModel", 2, true)]
    [Arguments("VehicleModel", 1, false)]
    [Arguments("ShipModel", 2, false)]
    public async Task Load_PassengerSeatNeedsMatchingModelBindingAndVehicleAnimation(string type, int animationPoint, bool expected)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO slaves VALUES (106, 'Comet', 1323, 't', 0, 0, 143, 50, 50, 5, 50, 0, 0, 'f', 5, 0, 0, 0);
            INSERT INTO models VALUES (1323, @type, 46);
            INSERT INTO model_bindings VALUES ('Model', 1323, 2);
            INSERT INTO attach_anims VALUES ('VehicleModel', 46, @point);
            """;
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@point", animationPoint);
        command.ExecuteNonQuery();
        var data = new SlaveGameData();
        data.Load(connection);

        await Assert.That(data.GetSlaveTemplate(106).HasPassengerSeat).IsEqualTo(expected);
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
        foreach (var id in new uint[] { 106, 112, 116 })
            await Assert.That(data.GetSlaveTemplate(id).HasPassengerSeat).IsTrue();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, mountable FROM slaves";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var template = data.GetSlaveTemplate((uint)reader.GetInt64(0));
            await Assert.That(template.Mountable).IsEqualTo(reader.GetString(1) == "t");
        }
    }

    [Test]
    public async Task ExactClient_SpeedcarPassengerTransformsMatchSharedNativeBone()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the native attachment check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            using var stream = source.GetFileStream("game/objects/env/06_unit/05_carriage/speedcar_body.chr");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bone = CryGeometryResolver.ReadCgf(buffer.ToArray()).CharacterBones.Single(bone => bone.Name == "passenger0");
            var data = new SlaveGameData();
            data.LoadSlaveAttachmentPointLocations();
            foreach (var model in new uint[] { 1323, 1371, 1406 })
            {
                var point = data.GetAttachPointsForSlave(model)[AttachPointKind.Passenger0];
                var rotation = PositionAndRotation.ToQuaternion(new Vector3(point.Roll, point.Pitch, point.Yaw));
                await Assert.That(Vector3.Distance(bone.BindTransform.Translation, point.AsPositionVector())).IsLessThan(0.00001f);
                foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    await Assert.That(Vector3.Distance(Vector3.Transform(axis, rotation),
                        Vector3.TransformNormal(axis, bone.BindTransform))).IsLessThan(0.00001f);
            }
        }
        finally { source.Close(); }
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
            CREATE TABLE models (id INTEGER, sub_type TEXT, sub_id INTEGER);
            CREATE TABLE model_bindings (owner_type TEXT, owner_id INTEGER, attach_point_id INTEGER);
            CREATE TABLE attach_anims (owner_type TEXT, owner_id INTEGER, attach_point_id INTEGER);
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
