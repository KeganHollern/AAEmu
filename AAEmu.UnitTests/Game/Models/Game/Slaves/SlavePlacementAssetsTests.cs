using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Slaves;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Slaves;

public sealed class SlavePlacementAssetsTests
{
    [Test]
    public async Task ExactClient_ActiveSummonModelsHaveBoundsExceptTheMissingRedBullPrefab()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        var compact = Environment.GetEnvironmentVariable("AAEMU_HOUSING_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(compact),
            "Set AAEMU_HOUSING_GAME_PAK and AAEMU_HOUSING_COMPACT for the exact-client vehicle check.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var data = new HousingGeometryGameData();
        data.Load(connection);
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            Stream Open(string name) => source.FileExists(name) ? source.GetFileStream(name) : null;
            var assets = new HousingGeometryAssets(Open, data.GetModelPaths);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT s.model_id,m.sub_type,MAX(CASE WHEN sk.id IS NULL THEN 0 ELSE 1 END) " +
                "FROM item_summon_slaves i JOIN slaves s ON s.id=i.slave_id JOIN models m ON m.id=s.model_id " +
                "LEFT JOIN items it ON it.id=i.item_id LEFT JOIN skills sk ON sk.id=it.use_skill_id " +
                "GROUP BY s.model_id,m.sub_type ORDER BY s.model_id";
            using var reader = command.ExecuteReader();
            var count = 0;
            var errors = new List<string>();
            var missingModels = new List<uint>();
            while (reader.Read())
            {
                var model = checked((uint)reader.GetInt64(0));
                var active = reader.GetInt32(2) != 0;
                await Assert.That(reader.GetString(1) is "VehicleModel" or "ShipModel").IsTrue();
                AAEmu.Game.Models.CryEngine.Physics.CryGeometryAsset asset;
                try
                {
                    asset = assets.LoadModel(model);
                }
                catch (Exception exception)
                {
                    var message = $"Vehicle model {model}, active {active}: {exception.Message}";
                    Console.WriteLine(message);
                    if (active)
                    {
                        errors.Add(message);
                        missingModels.Add(model);
                    }
                    continue;
                }
                if (!asset.HasModelBounds || !SlavePlacementRules.ValidBounds(asset.Bounds))
                {
                    var message = $"Vehicle model {model}, active {active}: No finite authored model bounds.";
                    Console.WriteLine(message);
                    if (active)
                    {
                        errors.Add(message);
                        missingModels.Add(model);
                    }
                    continue;
                }
                var box = SlavePlacementRules.PlacementBox(asset.Bounds, new(new(1000, 2000, 100), 1.25f));
                await Assert.That(SlavePlacementRules.IsFinite(box.Center)).IsTrue();
                var unresolved = asset.PoseRequirements.Count(pose => pose.Playing && pose.Physicalized && pose.AffectsCollision);
                Console.WriteLine($"Vehicle model {model}, active {active}: min {asset.Bounds.Min}, max {asset.Bounds.Max}, collision parts {asset.Parts.Count}, unresolved poses {unresolved}.");
                count++;
            }
            await Assert.That(count).IsGreaterThan(0);
            // The compact names speedcar.speedcar_body_redbull, but this exact archive has
            // only speedcar.speedcar_side_redbull. No substitute bounds are authoritative.
            await Assert.That(missingModels).IsEquivalentTo(new uint[] { 1427 });
            await Assert.That(errors.Single()).IsEqualTo("Vehicle model 1427, active True: No finite authored model bounds.");
        }
        finally
        {
            source.Close();
        }
    }
}
