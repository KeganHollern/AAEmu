using System.Text;

using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj.Static;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class MateSeatGameDataTests
{
    [Test]
    public async Task Load_UsesActorAliasesAndNormalizesClientPaths()
    {
        using var connection = CreateData();
        var requested = new List<string>();
        var data = new MateSeatGameData();
        data.Load(connection, path =>
        {
            requested.Add(path);
            return new MemoryStream(Encoding.UTF8.GetBytes("""
                <CharacterDefinition><AttachmentList>
                  <Attachment AName="bone_spine_driver" Type="CA_BONE"/>
                  <Attachment AName="$passenger0" Type="CA_BONE"/>
                </AttachmentList></CharacterDefinition>
                """));
        });
        await Assert.That(requested.Single()).IsEqualTo("game/objects/pet.cdf");
        await Assert.That(data.HasSeat(139, AttachPointKind.Driver)).IsTrue();
        await Assert.That(data.HasSeat(139, AttachPointKind.Passenger0)).IsFalse();
        await Assert.That(data.HasSeat(139, AttachPointKind.Passenger1)).IsFalse();
        await Assert.That(data.HasSeat(999, AttachPointKind.Driver)).IsFalse();
    }

    [Test]
    [Arguments("CA_BONE", true)]
    [Arguments("CA_SKIN", false)]
    public async Task Load_PassengerSeatRequiresABoneAttachment(string type, bool expected)
    {
        using var connection = CreateData();
        var data = new MateSeatGameData();
        data.Load(connection, _ => new MemoryStream(Encoding.UTF8.GetBytes(
            $"<CharacterDefinition><Attachment AName=\"bone_spine_passenger\" Type=\"{type}\"/></CharacterDefinition>")));
        await Assert.That(data.HasSeat(139, AttachPointKind.Passenger0)).IsEqualTo(expected);
        await Assert.That(data.HasSeat(139, AttachPointKind.Driver)).IsFalse();
    }

    [Test]
    public async Task Load_MissingModelAndReloadDoNotRetainASeat()
    {
        using var connection = CreateData();
        var data = new MateSeatGameData();
        data.Load(connection, _ => new MemoryStream(Encoding.UTF8.GetBytes(
            "<CharacterDefinition><Attachment AName=\"bone_spine_driver\" Type=\"CA_BONE\"/></CharacterDefinition>")));
        await Assert.That(data.HasSeat(139, AttachPointKind.Driver)).IsTrue();
        data.Load(connection, _ => null);
        await Assert.That(data.HasSeat(139, AttachPointKind.Driver)).IsFalse();
    }

    [Test]
    public async Task Load_ReviewedCompactAndExtractedModels()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        var assets = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_MATE_ASSETS");
        Skip.Unless(!string.IsNullOrEmpty(compact) && !string.IsNullOrEmpty(assets),
            "Set AAEMU_COMBAT_TEST_COMPACT and AAEMU_COMBAT_TEST_MATE_ASSETS for the r208022 model check.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = compact, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var data = new MateSeatGameData();
        data.Load(connection, path => File.OpenRead(Path.Combine(assets!, path)));
        await Assert.That(data.HasSeat(139, AttachPointKind.Driver)).IsTrue();
        await Assert.That(data.HasSeat(139, AttachPointKind.Passenger0)).IsTrue();
        await Assert.That(data.HasSeat(464, AttachPointKind.Driver)).IsFalse();
        await Assert.That(data.HasSeat(464, AttachPointKind.Passenger0)).IsFalse();
    }

    private static SqliteConnection CreateData()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE npcs(model_id INTEGER, mate_kind_id INTEGER);
            CREATE TABLE models(id INTEGER, sub_id INTEGER, sub_type TEXT);
            CREATE TABLE actor_models(id INTEGER, model_file TEXT);
            CREATE TABLE model_attach_point_strings(id INTEGER, actor TEXT, prefab TEXT);
            INSERT INTO npcs VALUES(139,1),(139,1),(464,1);
            INSERT INTO models VALUES(139,1,'ActorModel'),(464,2,'ActorModel');
            INSERT INTO actor_models VALUES(1,'Objects\PET.cdf'),(2,'objects/nonrideable.chr');
            INSERT INTO model_attach_point_strings VALUES(1,'bone_spine_driver','$driver'),(2,'bone_spine_passenger','$passenger0');
            """;
        command.ExecuteNonQuery();
        return connection;
    }
}
