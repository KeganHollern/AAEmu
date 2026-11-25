using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    private readonly record struct FootFace(CryWorldObjectInstance Brush, float HeightOffset, double Covered);

    // The single-mesh search already failed. Accept two distinct, coplanar faces only if their
    // projections cover the entire straight step and the space above the crossing is clear.
    private bool TryCrossPlacedFloorFaces(Npc npc, int cellX, int cellY, Vector3 from, Vector3 to,
        out float floor, NpcStepSurfaceTrace trace)
    {
        floor = default;
        if (npc.ModelId == 0 || npc.Template == null ||
            ModelManager.Instance.GetActorModel(npc.ModelId)?.Stances.TryGetValue(npc.CurrentGameStance, out var stance) != true)
            return false;
        try
        {
            var actor = CryActorGeometry.Create(stance, npc.Scale).Bounds;
            if (actor.Max.Z <= 0 || actor.HalfSize.X <= 0)
                return false;
            var min = Vector3.Min(from, to) + new Vector3(-actor.HalfSize.X, -actor.HalfSize.Y, -StaticFloorPrecision);
            var max = Vector3.Max(from, to) + new Vector3(actor.HalfSize.X, actor.HalfSize.Y, actor.Max.Z + StaticFloorPrecision);
            // Bound this local search to at most four spatial index buckets per cell.
            if (max.X - min.X > WorldManager.REGION_SIZE || max.Y - min.Y > WorldManager.REGION_SIZE)
                return false;
            var brushes = new List<CryWorldObjectInstance>();
            for (var y = cellY - 1; y <= cellY + 1; y++)
            for (var x = cellX - 1; x <= cellX + 1; x++)
            {
                var cell = worldTemplate.GetCell(x, y);
                if (cell?.LoadedObjectDat is { } objects)
                {
                    if (objects.HasUnparsedObjects)
                        return false;
                    brushes.AddRange(GetGroundBrushIndex(cell, objects).Query(min, max));
                }
            }
            if (brushes.Count < 2)
                return false;

            var starts = new List<FootFace>();
            var ends = new List<FootFace>();
            var solids = new List<(CryWorldObjectInstance Brush, CryGeometryPart Part)>();
            var unresolved = new List<CryWorldObjectInstance>();
            foreach (var brush in brushes)
            {
                var asset = TryReadBrush(brush);
                if (asset == null || asset.HasAnimatedCollision || asset.PoseRequirements.Any(pose => pose.AffectsCollision))
                {
                    unresolved.Add(brush);
                    continue;
                }
                foreach (var part in asset.Parts)
                {
                    if (part.PhysicsType != 0x1000)
                        continue;
                    solids.Add((brush, part));
                    if (part.Shape is not CryTriangleMesh mesh || brush.Min.Z > from.Z + StaticFloorPrecision ||
                        !brush.ModelUri.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (TryGetFootFace(brush, part, mesh, from, to, out var start))
                        starts.Add(start);
                    if (TryGetFootFace(brush, part, mesh, to, from, out var end))
                        ends.Add(end);
                }
            }

            foreach (var start in starts)
            foreach (var end in ends)
            {
                if (ReferenceEquals(start.Brush, end.Brush) || start.HeightOffset != end.HeightOffset || start.Covered + end.Covered < 1)
                    continue;
                var height = from.Z + end.HeightOffset;
                if (!IsStepClear(solids, unresolved, from, to, height, actor))
                    continue;
                floor = height;
                trace?.ObserveCrossing(start.Brush, end.Brush);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            Logger.Debug(ex, "Could not prove a crossing between static floors at {0} in {1}", from, worldTemplate.Name);
        }
        return false;
    }

    // The NPC's current stance and scale supply the height and radius. Align the clearance box with
    // the actual step. The corners of its world AABB are not part of the straight crossing.
    private static bool IsStepClear(List<(CryWorldObjectInstance Brush, CryGeometryPart Part)> solids,
        List<CryWorldObjectInstance> unresolved, Vector3 from, Vector3 to, float floor, CryBounds actor)
    {
        var step = to - from;
        var origin = new Vector3(from.X, from.Y, floor);
        var bottom = MathF.Max(MathF.BitIncrement(floor) - floor, actor.Min.Z);
        var top = actor.Max.Z;
        var center = step / 2;
        center.Z = bottom + (top - bottom) / 2;
        var transform = Matrix4x4.CreateRotationZ(MathF.Atan2(step.Y, step.X)) * Matrix4x4.CreateTranslation(center);
        var box = new CryBox(Vector3.Zero, new Vector3(step.Length() / 2 + actor.HalfSize.X,
            actor.HalfSize.Y, (top - bottom) / 2), Matrix4x4.Identity);
        foreach (var (brush, part) in solids)
        {
            if (brush.Max.Z - floor < bottom)
                continue;
            var placement = brush.Transform;
            placement.Translation -= origin;
            if (CryGeometryQueries.IntersectBox(part, placement, box, transform) != CryIntersection.Clear)
                return false;
        }
        foreach (var brush in unresolved)
        {
            var bounds = new CryBounds(brush.Min - origin, brush.Max - origin);
            var unknown = new CryGeometryPart(new CryBox(bounds.Center, bounds.HalfSize, Matrix4x4.Identity),
                Matrix4x4.Identity, 0x1000, "", "");
            if (CryGeometryQueries.IntersectBox(unknown, Matrix4x4.Identity, box, transform) != CryIntersection.Clear)
                return false;
        }
        return true;
    }

    private static double SideOfEdge(Vector3 a, Vector3 b, Vector3 p) =>
        ((double)b.X - a.X) * ((double)p.Y - a.Y) - ((double)b.Y - a.Y) * ((double)p.X - a.X);

    private static bool TryGetFootFace(CryWorldObjectInstance brush, CryGeometryPart part, CryTriangleMesh mesh,
        Vector3 foot, Vector3 other, out FootFace face)
    {
        face = default;
        if (foot.X < brush.Min.X || foot.X > brush.Max.X || foot.Y < brush.Min.Y || foot.Y > brush.Max.Y ||
            !TryFloorHit(part, brush.Transform, foot, false, out var hit))
            return false;
        var indices = mesh.Indices.AsSpan(hit.TriangleIndex * 3, 3);
        // Keep the sub-centimetre seam in the foot's frame. Adding each local vertex to a
        // large world coordinate first can round two distinct boundaries onto the same point.
        var placement = brush.Transform;
        placement.Translation -= foot;
        var transform = part.Transform * placement;
        Span<Vector3> vertices = stackalloc Vector3[3];
        for (var i = 0; i < 3; i++)
            vertices[i] = Vector3.Transform(mesh.Vertices[indices[i]], transform);
        if (vertices[0].Z != vertices[1].Z || vertices[1].Z != vertices[2].Z)
            return false;

        // Clip [foot, other] against the three closed half-planes of this triangle. Use the
        // relative vertices and double products. A real horizontal gap has no tolerance.
        var step = other - foot;
        var orientation = Math.Sign(SideOfEdge(vertices[0], vertices[1], vertices[2]));
        if (orientation == 0)
            return false;
        var coverage = 1d;
        for (var i = 0; i < 3; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % 3];
            var start = orientation * SideOfEdge(a, b, Vector3.Zero);
            if (start < 0)
                return false;
            var end = orientation * SideOfEdge(a, b, step);
            if (end < 0)
                coverage = Math.Min(coverage, start / (start - end));
        }
        face = new FootFace(brush, vertices[0].Z, coverage);
        return true;
    }
}
