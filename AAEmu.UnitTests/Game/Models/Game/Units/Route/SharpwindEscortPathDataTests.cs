using System.Globalization;
using System.Numerics;

namespace AAEmu.UnitTests.Game.Models.Game.Units.Route;

/// <summary>
/// Guards the waypoint data behind the Sharpwind Mines entrance beat.
/// ai_command_set 185 (칼바람폐광_알리스테어0) ends with FollowPath "aipath_alistair0_0" followed by
/// the self-delete skill; a missing or unwalkable file makes the whole command set die on the spot.
/// </summary>
public class SharpwindEscortPathDataTests
{
    private const string PathName = "aipath_alistair0_0";

    // instance_cuttingwind_deadmine (zone 262) playable envelope.
    private const float MinX = 460f, MaxX = 810f;
    private const float MinY = 300f, MaxY = 650f;
    private const float MinZ = 135f, MaxZ = 250f;

    // Mine-mouth plateau: the only navmesh island at the top of the shaft (zone 262 BAI nodes
    // span X 742..758, Y 321..331, Z 247.67..249.13). Npc.MoveTowards/Simulation.MoveTo overwrite
    // the waypoint Z with WorldManager.GetReferenceHeight every tick, so a waypoint outside that
    // height band can never be reached and the AI command set stalls instead of despawning.
    private const float PlateauMinZ = 246f, PlateauMaxZ = 250f;

    // Allistair 12108's spawn and the western navmesh edge he walks to before dropping in.
    private static readonly Vector2 SpawnXy = new(749.7f, 325.8f);
    private const float ShaftLipMaxX = 743f;

    private static string PathFileName =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Path", PathName + ".path");

    /// <summary>
    /// Mirrors AiPathsManager.LoadAiPathPoints: split on '|', require 5 columns, columns 1..3 are X/Y/Z.
    /// </summary>
    private static List<Vector3> ReadWaypoints()
    {
        var points = new List<Vector3>();
        foreach (var line in File.ReadAllLines(PathFileName))
        {
            var columns = line.Split('|');
            if (columns.Length != 5)
                continue;

            if (!float.TryParse(columns[1], CultureInfo.InvariantCulture, out var x))
                continue;
            if (!float.TryParse(columns[2], CultureInfo.InvariantCulture, out var y))
                continue;
            if (!float.TryParse(columns[3], CultureInfo.InvariantCulture, out var z))
                continue;

            points.Add(new Vector3(x, y, z));
        }

        return points;
    }

    [Test]
    public async Task AlistairEntrancePath_IsShippedAndParsesAsWaypoints()
    {
        await Assert.That(File.Exists(PathFileName)).IsTrue();

        var lines = File.ReadAllLines(PathFileName).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var waypoints = ReadWaypoints();

        // Every non-blank line has to survive the engine parser, otherwise waypoints are silently dropped.
        await Assert.That(waypoints.Count).IsEqualTo(lines.Count);
        await Assert.That(waypoints.Count).IsGreaterThanOrEqualTo(2);

        for (var i = 1; i < waypoints.Count; i++)
        {
            var step = (waypoints[i] - waypoints[i - 1]).Length();
            await Assert.That(step).IsLessThanOrEqualTo(10f);
        }
    }

    [Test]
    public async Task AlistairEntrancePath_WalksTheMineMouthPlateauToTheShaftLip()
    {
        var waypoints = ReadWaypoints();

        foreach (var waypoint in waypoints)
        {
            await Assert.That(waypoint.X).IsGreaterThanOrEqualTo(MinX).And.IsLessThanOrEqualTo(MaxX);
            await Assert.That(waypoint.Y).IsGreaterThanOrEqualTo(MinY).And.IsLessThanOrEqualTo(MaxY);
            await Assert.That(waypoint.Z).IsGreaterThanOrEqualTo(MinZ).And.IsLessThanOrEqualTo(MaxZ);

            // The descent into the pit is not navmesh; every waypoint must stay on the plateau.
            await Assert.That(waypoint.Z).IsGreaterThanOrEqualTo(PlateauMinZ).And.IsLessThanOrEqualTo(PlateauMaxZ);
        }

        var first = waypoints[0];
        var last = waypoints[^1];

        // The beat starts where Allistair 12108 stands, so Simulation picks up checkpoint 0 first.
        await Assert.That(new Vector2(first.X - SpawnXy.X, first.Y - SpawnXy.Y).Length()).IsLessThanOrEqualTo(1f);

        // ...and leads west, to the drop-off edge above the water.
        await Assert.That(last.X).IsLessThanOrEqualTo(first.X - 1f);
        await Assert.That(last.X).IsLessThanOrEqualTo(ShaftLipMaxX);
    }
}
