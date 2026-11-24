using System.Globalization;
using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers;

// A command creates this for one prospective step. Normal movement does not collect a trace.
public sealed class NpcStepSurfaceTrace
{
    private string _selection;
    private string _staticCheck = "NotNeeded";
    private CryWorldObjectInstance _brush;
    private (int X, int Y) _cell;
    private int _part = -1;
    private int _startFace = -1;
    private int _endFace = -1;
    private FloorSearch _progress;

    private NpcStepSurfaceTrace() { }

    public static IEnumerable<string> DescribeStep(Npc npc, Vector3 target, float distance)
    {
        if (npc.Ai == null || npc.Transform.Parent != null || npc.Transform.StickyParent != null)
        {
            yield return "A prospective world-space step needs an unparented NPC with AI.";
            yield break;
        }
        var from = npc.Transform.Local.Position;
        var range = MathUtil.CalculateDistance(from, target, true);
        if (!float.IsFinite(range) || range == 0f)
        {
            yield return "The destination must have a finite, nonzero distance from the NPC.";
            yield break;
        }
        var next = Npc.GetRequestedStep(from, target, distance, range);
        var trace = new NpcStepSurfaceTrace();
        var height = npc.ResolveStepHeight(from, target, next, trace);
        yield return $"Prospective ad-hoc step height for obj={npc.ObjId} from={FormatStepPosition(from)} target={FormatStepPosition(target)} requested={FormatStepPosition(next)}";
        yield return $"selected={trace._selection} z={height.ToString("F3", CultureInfo.InvariantCulture)} staticCheck={trace._staticCheck}";
        if (trace._brush != null)
        {
            yield return $"ownerCell=({trace._cell.X},{trace._cell.Y}) placement={trace._brush.Source}";
            yield return $"model={trace._brush.ModelUri}";
            if (trace._part >= 0)
                yield return $"solidPart={trace._part} startFace={FormatIndex(trace._startFace)} endFace={FormatIndex(trace._endFace)}";
        }
    }

    private static string FormatStepPosition(Vector3 value) =>
        string.Create(CultureInfo.InvariantCulture, $"({value.X:R},{value.Y:R},{value.Z:R})");

    private static string FormatIndex(int value) => value >= 0 ? value.ToString(CultureInfo.InvariantCulture) : "n/a";

    internal void Select(string selection) => _selection = selection;
    internal void Skip(string reason) => _staticCheck = reason;

    // Keep the furthest stage the search reached if several placed meshes overlap these feet.
    internal void Observe(FloorSearch stage, WorldCell cell, CryWorldObjectInstance brush = null,
        int part = -1, int startFace = -1, int endFace = -1)
    {
        if (stage <= _progress)
            return;
        _progress = stage;
        _staticCheck = stage.ToString();
        _brush = brush;
        _cell = (cell.CellX, cell.CellY);
        _part = part;
        _startFace = startFace;
        _endFace = endFace;
    }

    internal enum FloorSearch
    {
        None, NoSharedBrush, DestinationOutsideBrush, GeometryUnavailable, NoStaticSolidMesh, NoStartFace, NoEndFace,
        FacesDisconnectedAlongStep, ContinuousStaticFloor
    }
}
