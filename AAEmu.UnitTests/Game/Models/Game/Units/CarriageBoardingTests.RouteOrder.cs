using System.Numerics;
using System.Xml;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Transfers;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.World;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class CarriageBoardingTests
{
    [Test]
    public async Task CompactRowOrder_DoesNotReorderStopsOrChangeTheirWaits()
    {
        SetSingleton(new WorldManager(null, null, null, null, null));
        SetSingleton(new LocalizationManager());
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var query = db.CreateCommand();
        // The Game compact stores these rows in this order. The stock client starts White Arden at leg 41.
        query.CommandText = """
            CREATE TABLE transfers(id INT, comment TEXT, model_id INT, wait_time REAL, cyclic NUM, path_smoothing REAL);
            INSERT INTO transfers VALUES (10, 'White Arden', 654, 10, 1, 8);
            CREATE TABLE transfer_bindings(id INT);
            CREATE TABLE transfer_binding_doodads(id INT);
            CREATE TABLE transfer_paths(id INT PRIMARY KEY, owner_id INT, owner_type TEXT, path_name TEXT, wait_time_start REAL, wait_time_end REAL);
            INSERT INTO transfer_paths VALUES
              (336,10,'Transfer','t_roadend_whiteB',0,0), (41,10,'Transfer','whiteforest_roy_out_1',0,0),
              (42,10,'Transfer','whiteforest_roy_out_2',0,0), (421,10,'Transfer','roy_whiteforest_in_1',0,0),
              (422,10,'Transfer','roy_whiteforest_in_2',0,20), (43,10,'Transfer','t_whiteB_roadend',0,20);
            """;
        query.ExecuteNonQuery();
        var data = new TransferGameData();
        data.Load(db);
        var paths = data.GetTransferTemplate(10).TransferAllPaths;
        await Assert.That(paths.Select(p => p.Id).SequenceEqual(new uint[] { 41, 42, 43, 336, 421, 422 })).IsTrue();
        await Assert.That(paths.Where(p => p.WaitTimeEnd == 20).Select(p => p.Id).SequenceEqual(new uint[] { 43, 422 })).IsTrue();
    }

    [Test]
    [Arguments(10u, 41u, 11650f, 12960f, 127f, 33f)]
    [Arguments(14u, 56u, 17786f, 8367f, 260f, 6f)]
    [Arguments(109u, 562u, 16890f, 8044f, 146f, 3f)]
    public async Task StockCarriage_StopsAtBothStationsAndCompletesItsAuthoredCircuit(
        uint id, uint firstLeg, float farX, float farY, float farZ, float largestJoin)
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        var archive = Environment.GetEnvironmentVariable("AAEMU_REDBULL_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(compact) && !string.IsNullOrEmpty(archive), "Supply the r208022 server compact and client game_pak.");
        var world = SetUpStation(cells: 21);
        var template = LoadStockCarriage(id, compact, archive);
        await Assert.That(template.TransferAllPaths[0].Id).IsEqualTo(firstLeg);
        await Assert.That(template.TransferRoads[0].Type).IsEqualTo((int)id);
        await Assert.That(template.TransferRoads.Select(road => road.Name)
            .SequenceEqual(template.TransferAllPaths.Select(leg => leg.PathName))).IsTrue();
        for (var leg = 0; leg < template.TransferRoads.Count; leg++)
        {
            var end = template.TransferRoads[leg].Pos[^1].AsPositionVector();
            var next = template.TransferRoads[(leg + 1) % template.TransferRoads.Count].Pos[0].AsPositionVector();
            await Assert.That(Vector3.Distance(end, next)).IsLessThan(largestJoin);
        }

        ConfigureSpawn(template);
        world.TransferManager = new TransferManager();
        var file = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "transfer_spawns.json");
        var spawner = JsonHelper.DeserializeObject<List<TransferSpawner>>(await File.ReadAllTextAsync(file)).Single(s => s.UnitId == id);
        spawner.ParentWorld = world;
        var carriage = spawner.Spawn(0);
        var start = carriage.Transform.World.Position;
        await Assert.That(Vector3.Distance(start, spawner.Position.AsPositionVector())).IsLessThan(0.1f);

        var visits = new List<int> { 0 };
        var farStop = false;
        var returned = false;
        for (var tick = 0; tick < 10000 && !returned; tick++)
        {
            carriage.WaitTime = DateTime.MinValue;
            carriage.MoveTo(carriage);
            if (carriage.Steering != visits[^1])
                visits.Add(carriage.Steering);
            var pos = carriage.Transform.World.Position;
            if (carriage.TimeLeft > 19000 && Vector3.Distance(pos, new Vector3(farX, farY, farZ)) < 6)
                farStop = true;
            returned = visits.Count == template.TransferRoads.Count + 1 && carriage.TimeLeft > 9000 &&
                Vector3.Distance(pos, start) < 2;
        }
        await Assert.That(visits.SequenceEqual(Enumerable.Range(0, template.TransferRoads.Count).Append(0))).IsTrue();
        await Assert.That(farStop).IsTrue();
        await Assert.That(returned).IsTrue();
    }

    [Test]
    public async Task StockMarianopleTram_HoldsBothBenchesWithinReachOfTheStationPlatforms()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        var archive = Environment.GetEnvironmentVariable("AAEMU_REDBULL_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(compact) && !string.IsNullOrEmpty(archive), "Supply the r208022 server compact and client game_pak.");
        var world = SetUpStation(modelId: 606, vehicleModelId: 20);
        var template = LoadStockCarriage(3, compact, archive);
        ConfigureSpawn(template, cabinModelId: 314, seatId: 5921, phaseId: 15276);
        world.TransferManager = new TransferManager();
        var file = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "transfer_spawns.json");
        var spawner = JsonHelper.DeserializeObject<List<TransferSpawner>>(await File.ReadAllTextAsync(file)).Single(s => s.UnitId == 3);
        spawner.ParentWorld = world;
        var motor = spawner.Spawn(0);
        // Walkable points on the stock Cerin bridge station and Borland station, beside the two benches.
        var platforms = new[]
        {
            (Front: new Vector3(11135.40f, 11823.236f, 136.5282f), Rear: new Vector3(11128.198f, 11824.785f, 136.5282f)),
            (Front: new Vector3(11377.195f, 12084.033f, 137.1347f), Rear: new Vector3(11376.262f, 12076.479f, 137.1347f))
        };
        var checkedStops = 0;
        for (var tick = 0; tick < 2000 && checkedStops < platforms.Length; tick++)
        {
            motor.WaitTime = DateTime.MinValue;
            motor.MoveTo(motor);
            if (motor.Steering != checkedStops + 1 || motor.TimeLeft < 14000)
                continue;

            var bench0 = motor.Bounded.AttachedDoodads.Single(seat => seat.AttachPoint == AttachPointKind.Passenger0);
            var bench1 = motor.Bounded.AttachedDoodads.Single(seat => seat.AttachPoint == AttachPointKind.Passenger1);
            var (front, rear) = platforms[checkedStops];
            // Both public benches use skill 15857, whose maximum range is four metres.
            await Assert.That(Vector3.Distance(bench0.Transform.World.Position, front)).IsLessThan(4);
            await Assert.That(Vector3.Distance(bench1.Transform.World.Position, rear)).IsLessThan(4);
            var parked = motor.Bounded.Transform.World.Position;
            motor.MoveTo(motor);
            await CheckPoint(motor.Bounded.Transform.World.Position, parked);
            checkedStops++;
        }
        await Assert.That(checkedStops).IsEqualTo(2);
    }

    private TransferTemplate LoadStockCarriage(uint id, string compact, string archive)
    {
        if (!ClientFileManager.AddSource(archive))
            throw new IOException("Could not open the r208022 client archive.");
        var source = ClientFileManager.Sources[^1];
        try
        {
            const string name = "main_world";
            var template = new WorldTemplate();
            var xml = new XmlDocument();
            using var stream = ClientFileManager.GetFileStream($"game/worlds/{name}/world.xml");
            xml.Load(stream);
            template.XmlWorld.ReadNode(xml.DocumentElement, template);
            var worlds = WorldManager.Instance;
            worlds.WorldTemplates.Add(name, template);
            SetField(worlds, "<WorldNames>k__BackingField", new List<string> { name });
            SetField(worlds, "_worldIdByZoneKey", template.ZoneKeys.ToDictionary(zone => zone, _ => template.Id));
            SetSingleton(new LocalizationManager());
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            var data = new TransferGameData();
            data.Load(db);
            return data.GetTransferTemplate(id);
        }
        finally
        {
            ((IList<ClientSource>)ClientFileManager.Sources).Remove(source);
            source.Close();
        }
    }
}
