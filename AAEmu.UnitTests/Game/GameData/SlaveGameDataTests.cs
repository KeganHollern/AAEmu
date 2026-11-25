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
    public async Task ExactClient_CarsSharingAnAttachmentModelKeepTheirOwnBindings()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var data = new SlaveGameData();
        data.Load(connection);

        foreach (var (primary, display, model, hood) in new[]
                 { (106u, 117u, 1323u, 6445u), (112u, 118u, 1371u, 6771u), (116u, 119u, 1406u, 6772u) })
        {
            var car = data.GetSlaveTemplate(primary);
            var preview = data.GetSlaveTemplate(display);
            await Assert.That(car.ModelId).IsEqualTo(model);
            await Assert.That(preview.ModelId).IsEqualTo(model);
            await Assert.That(preview.DoodadBindings.Single().DoodadId).IsEqualTo(hood);
            await Assert.That(car.DoodadBindings.Single(d => d.AttachPointId == AttachPointKind.Sail0).DoodadId).IsEqualTo(hood);
            foreach (var socket in new[] { AttachPointKind.Box0, AttachPointKind.Box1 })
                await Assert.That(car.DoodadBindings.Single(d => d.AttachPointId == socket && d.Persist).DoodadId).IsEqualTo(3446u);
            await Assert.That(car.HealingPointDoodads.Single().DoodadId).IsEqualTo(6444u);
            await Assert.That(preview.HealingPointDoodads.Single().DoodadId).IsEqualTo(6444u);
        }

        var archeOne = data.GetSlaveTemplate(122);
        await Assert.That(archeOne.ModelId).IsEqualTo(1323u);
        var binding = archeOne.DoodadBindings.Single();
        await Assert.That(binding.AttachPointId).IsEqualTo(AttachPointKind.Sail0);
        await Assert.That(binding.DoodadId).IsEqualTo(7162u);
        await Assert.That(archeOne.HealingPointDoodads).IsEmpty();
    }

    [Test]
    [Arguments("AAEMU_HOUSING_GAME_PAK", "speedcar_body.chr")]
    [Arguments("AAEMU_HOUSING_GAME_PAK", "speedcar_body_25.chr")]
    [Arguments("AAEMU_REDBULL_GAME_PAK", "speedcar_body.chr")]
    [Arguments("AAEMU_REDBULL_GAME_PAK", "speedcar_body_25.chr")]
    public async Task ExactClient_SpeedcarAttachmentsMatchTheirNativeBones(string archive, string body)
    {
        var path = Environment.GetEnvironmentVariable(archive);
        Skip.Unless(!string.IsNullOrEmpty(path), $"Set {archive} for the native attachment check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            using var stream = source.GetFileStream("game/objects/env/06_unit/05_carriage/" + body);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bones = CryGeometryResolver.ReadCgf(buffer.ToArray()).CharacterBones.ToDictionary(bone => bone.Name);
            var helpers = new Dictionary<AttachPointKind, string>
            {
                [AttachPointKind.Driver] = "driver", [AttachPointKind.Passenger0] = "passenger0",
                [AttachPointKind.Sail0] = "sail0", [AttachPointKind.HealPoint0] = "heal_point0",
                [AttachPointKind.Box0] = "box0", [AttachPointKind.Box1] = "box1"
            };
            var data = new SlaveGameData();
            data.LoadSlaveAttachmentPointLocations();
            foreach (var model in new uint[] { 1323, 1371, 1406 })
            {
                var points = data.GetAttachPointsForSlave(model);
                foreach (var (kind, name) in helpers)
                {
                    var bone = bones[name];
                    var point = points[kind];
                    var rotation = PositionAndRotation.ToQuaternion(new Vector3(point.Roll, point.Pitch, point.Yaw));
                    await Assert.That(Vector3.Distance(bone.BindTransform.Translation, point.AsPositionVector())).IsLessThan(0.00001f);
                    foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                        await Assert.That(Vector3.Distance(Vector3.Transform(axis, rotation),
                            Vector3.TransformNormal(axis, bone.BindTransform))).IsLessThan(0.00001f);
                }
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
