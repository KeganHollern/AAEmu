using System.Globalization;
using System.Text.Json;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.AI.v2.Controls;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public class RedDragonEncounterDataTests
{
    private const string RouteName = "AIPath_145_ruhasin_131";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static string DataPath(params string[] parts)
    {
        return Path.Combine([AppContext.BaseDirectory, "Data", .. parts]);
    }

    [Test]
    public async Task RedDragon_IsOneShotPinnedSpawnAboveEggWithFlightRoute()
    {
        var dragons = ReadSpawns("Worlds", "main_world", "npc_spawns.json")
            .Where(spawn => spawn.UnitId == 12411)
            .ToList();
        var eggs = ReadSpawns("Worlds", "main_world", "doodad_spawns.json")
            .Where(spawn => spawn.UnitId == 7056)
            .ToList();

        await Assert.That(dragons.Count).IsEqualTo(1);
        await Assert.That(eggs.Count).IsEqualTo(1);

        var dragon = dragons[0];
        var egg = eggs[0];
        await Assert.That(dragon.NpcSpawnerIds).IsNotNull();
        await Assert.That(dragon.NpcSpawnerIds.Count).IsEqualTo(1);
        await Assert.That(dragon.NpcSpawnerIds[0]).IsEqualTo(120194u);
        await Assert.That(dragon.FollowPath).IsEqualTo(RouteName);
        await Assert.That(dragon.Position.X).IsEqualTo(egg.Position.X);
        await Assert.That(dragon.Position.Y).IsEqualTo(egg.Position.Y);
        await Assert.That(dragon.Position.Z).IsEqualTo(200f);
        await Assert.That(dragon.Position.Z).IsGreaterThan(egg.Position.Z);
    }

    [Test]
    public async Task RedDragonFlightRoute_IsClosedAndExplicitlyLoopsAtAltitude()
    {
        var pathFile = DataPath("Path", RouteName + ".path");
        var points = File.ReadAllLines(pathFile)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseRoutePoint)
            .ToList();
        var enginePoints = AiPathsManager.Instance.LoadAiPathPoints(RouteName);

        await Assert.That(points.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(enginePoints.Count).IsEqualTo(points.Count);
        await Assert.That(points[^1].Action).IsEqualTo("EnableLoop");
        await Assert.That(enginePoints[^1].Action).IsEqualTo(AiPathPointAction.EnableLoop);
        await Assert.That(points[0].X).IsEqualTo(points[^1].X);
        await Assert.That(points[0].Y).IsEqualTo(points[^1].Y);

        foreach (var point in points)
            await Assert.That(point.Z).IsEqualTo(200f);
    }

    private static List<SpawnProbe> ReadSpawns(params string[] parts)
    {
        var json = File.ReadAllText(DataPath(parts));
        return JsonSerializer.Deserialize<List<SpawnProbe>>(json, JsonOptions) ?? [];
    }

    private static RoutePoint ParseRoutePoint(string line)
    {
        var columns = line.Split('|');
        if (columns.Length != 5)
            throw new InvalidDataException($"Invalid Red Dragon path row: {line}");

        return new RoutePoint(
            columns[0],
            float.Parse(columns[1], CultureInfo.InvariantCulture),
            float.Parse(columns[2], CultureInfo.InvariantCulture),
            float.Parse(columns[3], CultureInfo.InvariantCulture));
    }

    private sealed class SpawnProbe
    {
        public uint UnitId { get; set; }
        public List<uint> NpcSpawnerIds { get; set; }
        public string FollowPath { get; set; }
        public PositionProbe Position { get; set; }
    }

    private sealed class PositionProbe
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    private sealed record RoutePoint(string Action, float X, float Y, float Z);
}
