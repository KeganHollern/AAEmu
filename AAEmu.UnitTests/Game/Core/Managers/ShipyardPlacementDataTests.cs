using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Shipyard;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class ShipyardPlacementDataTests
{
    [Test]
    public async Task DesignMapping_RejectsDuplicateOrZeroTargets()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE item_shipyards(item_id INTEGER,shipyard_id INTEGER); INSERT INTO item_shipyards VALUES(28013,15)";
        command.ExecuteNonQuery();
        var mapping = ShipyardManager.ReadDesignShipyards(connection);
        await Assert.That(mapping[28013]).IsEqualTo(15u);
        command.CommandText = "INSERT INTO item_shipyards VALUES(28013,14)";
        command.ExecuteNonQuery();
        await Assert.That(() => ShipyardManager.ReadDesignShipyards(connection)).Throws<InvalidDataException>();
        command.CommandText = "DELETE FROM item_shipyards; INSERT INTO item_shipyards VALUES(28013,0)";
        command.ExecuteNonQuery();
        await Assert.That(() => ShipyardManager.ReadDesignShipyards(connection)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ExactClient_AllMappedPreviewModelsHaveAuthoritativeBounds()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        var compact = Environment.GetEnvironmentVariable("AAEMU_HOUSING_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(compact),
            "Set AAEMU_HOUSING_GAME_PAK and AAEMU_HOUSING_COMPACT for the exact-client shipyard check.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var mapping = ShipyardManager.ReadDesignShipyards(connection);
        await Assert.That(mapping.Count).IsEqualTo(13);
        await Assert.That(mapping[28013]).IsEqualTo(15u);
        var data = new HousingGeometryGameData();
        data.Load(connection);
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            Stream Open(string name) => source.FileExists(name) ? source.GetFileStream(name) : null;
            var assets = new HousingGeometryAssets(Open, data.GetModelPaths);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT s.id,p.model_id FROM shipyards s JOIN shipyard_steps p ON p.shipyard_id=s.id " +
                "JOIN item_shipyards i ON i.shipyard_id=s.id WHERE p.step=0 ORDER BY s.id";
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                var id = checked((uint)reader.GetInt64(0));
                var model = checked((uint)reader.GetInt64(1));
                var asset = assets.LoadModel(model);
                await Assert.That(asset.HasModelBounds).IsTrue();
                await Assert.That(ShipyardPlacementRules.ValidBounds(asset.Bounds)).IsTrue();
                await Assert.That(asset.Parts.Count).IsGreaterThan(0);
                var box = ShipyardPlacementRules.ClearanceBox(asset.Bounds, new(1000, 2000, 109.9f), 0, 26);
                await Assert.That(ShipyardPlacementRules.IsFinite(box.Center)).IsTrue();
                Console.WriteLine($"Shipyard {id}: model {model}, min {asset.Bounds.Min}, max {asset.Bounds.Max}, collision parts {asset.Parts.Count}.");
                count++;
            }
            await Assert.That(count).IsGreaterThan(0);
        }
        finally
        {
            source.Close();
        }
    }
}
