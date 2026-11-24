using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Core.Managers;

// A command creates this for one prospective step. Normal movement does not collect a trace.
internal sealed class NpcStepSurfaceTrace
{
    internal string Selection { get; private set; }
    internal string StaticCheck { get; private set; } = "NotNeeded";
    internal CryWorldObjectInstance Brush { get; private set; }
    internal (int X, int Y) Cell { get; private set; }
    internal int Part { get; private set; } = -1;
    internal int StartFace { get; private set; } = -1;
    internal int EndFace { get; private set; } = -1;
    private FloorSearch _progress;

    internal void Select(string selection) => Selection = selection;
    internal void Skip(string reason) => StaticCheck = reason;

    // Keep the furthest stage the search reached if several placed meshes overlap these feet.
    internal void Observe(FloorSearch stage, WorldCell cell, CryWorldObjectInstance brush = null,
        int part = -1, int startFace = -1, int endFace = -1)
    {
        if (stage <= _progress)
            return;
        _progress = stage;
        StaticCheck = stage.ToString();
        Brush = brush;
        Cell = (cell.CellX, cell.CellY);
        Part = part;
        StartFace = startFace;
        EndFace = endFace;
    }

    internal enum FloorSearch
    {
        None, NoSharedBrush, DestinationOutsideBrush, GeometryUnavailable, NoStaticSolidMesh, NoStartFace, NoEndFace,
        FacesDisconnectedAlongStep, ContinuousStaticFloor
    }
}
