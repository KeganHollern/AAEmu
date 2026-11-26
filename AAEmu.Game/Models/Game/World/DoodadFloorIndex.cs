using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.World;

// One index per world instance. A floor can extend across its origin's region boundary.
internal sealed class DoodadFloorIndex
{
    internal sealed record Entry(Doodad Doodad, Region Region, Vector3 Origin, CryBounds Bounds);
    private readonly record struct Cell(int X, int Y, int Z)
    {
        internal static Cell At(Vector3 p) => new((int)MathF.Floor(p.X / 4), (int)MathF.Floor(p.Y / 4), (int)MathF.Floor(p.Z / 4));
    }

    private readonly ConcurrentDictionary<Cell, Entry[]> _cells = [];
    private readonly Dictionary<Doodad, Entry> _entries = [];
    private readonly Lock _gate = new();

    internal ReadOnlySpan<Entry> At(Vector3 foot) => _cells.GetValueOrDefault(Cell.At(foot));

    internal void Update(Doodad doodad, Region region)
    {
        lock (_gate)
        {
            var next = Measure(doodad, region);
            if (_entries.TryGetValue(doodad, out var previous))
            {
                if (previous == next)
                    return;
                Unindex(previous);
            }
            if (next == null)
                return;
            _entries.Add(doodad, next);
            foreach (var cell in Cells(next.Bounds))
                _cells[cell] = _cells.TryGetValue(cell, out var old) ? [.. old, next] : [next];
        }
    }

    internal void Remove(Doodad doodad, Region region)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(doodad, out var previous) && ReferenceEquals(previous.Region, region))
                Unindex(previous);
        }
    }

    private void Unindex(Entry entry)
    {
        _entries.Remove(entry.Doodad);
        foreach (var cell in Cells(entry.Bounds))
        {
            var old = _cells[cell];
            if (old.Length == 1)
                _cells.TryRemove(cell, out _);
            else
                _cells[cell] = Array.FindAll(old, other => !ReferenceEquals(other, entry));
        }
    }

    private static Entry Measure(Doodad doodad, Region region)
    {
        if (!Region.IsStaticDoodadCandidate(doodad) || !(doodad.Scale > 0))
            return null;
        var origin = doodad.Transform.World.Position;
        if (origin != doodad.Spawner.Position.AsPositionVector())
            return null;
        var model = StockDoodadFloorModel.Find(HousingGeometryAssets.DoodadModelPath(doodad.Template, doodad.FuncGroupId));
        if (model == null)
            return null;
        var extent = Vector3.Max(Vector3.Abs(model.Bounds.Min), Vector3.Abs(model.Bounds.Max)) * doodad.Scale;
        if (extent.LengthSquared() <= 4)
            return null;
        var bounds = model.Bounds.Transform(HousingGeometryAssets.Transform(doodad));
        var margin = new Vector3(FallMovement.PositionTolerance);
        bounds = new(bounds.Min - margin, bounds.Max + margin);
        if (!float.IsFinite(bounds.Min.LengthSquared()) || !float.IsFinite(bounds.Max.LengthSquared()))
            return null;

        // The largest signed placement occupies 486 cells. Keep an oversized custom scale on the close path.
        var min = Cell.At(bounds.Min);
        var max = Cell.At(bounds.Max);
        var count = ((double)max.X - min.X + 1) * ((double)max.Y - min.Y + 1) * ((double)max.Z - min.Z + 1);
        return count is >= 1 and <= 512 ? new Entry(doodad, region, origin, bounds) : null;
    }

    private static IEnumerable<Cell> Cells(CryBounds bounds)
    {
        var min = Cell.At(bounds.Min);
        var max = Cell.At(bounds.Max);
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
        for (var z = min.Z; z <= max.Z; z++)
            yield return new Cell(x, y, z);
    }
}
