using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;
using static AAEmu.Game.Core.Managers.NpcStepSurfaceTrace.FloorSearch;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    internal const float StaticFloorPrecision = 0.01f;
    private const float CaveFloorReferenceSpan = 0.5f;
    private readonly ConditionalWeakTable<ObjectsFile, CryWorldObjectIndex> _groundBrushes = new();
    internal Func<CryWorldObjectInstance, CryGeometryAsset> GroundBrushGeometry { get; init; } =
        new CryWorldGeometryResolver(new CryGeometryResolver(ClientFileManager.GetFileStream).Load).Load;

    // A horizontal NPC step can leave a waypoint's region of influence while still on its solid deck.
    // Require continuous static collision between the two feet before replacing the selected ground.
    internal bool TryFollowStaticFloor(Npc npc, Vector3 from, Vector3 to, GroundSurfaceSource selectedSource, out float floor, NpcStepSurfaceTrace trace = null, Vector3? inclinedTarget = null)
    {
        floor = default;
        var (cellX, cellY) = from.ToCellIndex();
        var (nextX, nextY) = to.ToCellIndex();
        if (Math.Abs(cellX - nextX) > 1 || Math.Abs(cellY - nextY) > 1)
        {
            trace?.Skip("StepOutsideNeighborCells");
            return false;
        }
        if (!worldTemplate.TryGetHeight(from.X, from.Y, out var terrain))
        {
            trace?.Skip("StartTerrainUnavailable");
            return false;
        }
        // At a cave mouth, terrain can cross the solid floor between the two feet.
        // A walker already on the terrain needs no brush lookup.
        var separation = from.Z - terrain;
        if (MathF.Abs(separation) <= StaticFloorPrecision)
        {
            trace?.Skip("StartFootOnTerrain");
            return false;
        }
        if (separation < 0f && selectedSource != GroundSurfaceSource.Terrain)
        {
            trace?.Skip("StartFootBelowTerrainWithNavigation");
            return false;
        }
        if (inclinedTarget.HasValue && separation >= 0f)
        {
            trace?.Skip("InclinedTargetRequiresCaveFloor");
            return false;
        }
        trace?.Skip("NoLoadedBrushData");

        var belowTerrain = separation < 0f;
        var mayLeaveBrush = false;
        if (TryFollowFloorInCell(worldTemplate.GetCell(cellX, cellY), from, to, belowTerrain, ref mayLeaveBrush, out floor, trace, inclinedTarget))
            return true;

        // A brush belongs to one cell even where its floor extends into the next cell.
        // Use only loaded object data. The same solid part must support both feet.
        for (var y = cellY - 1; y <= cellY + 1; y++)
        for (var x = cellX - 1; x <= cellX + 1; x++)
        {
            if ((x != cellX || y != cellY) &&
                TryFollowFloorInCell(worldTemplate.GetCell(x, y), from, to, belowTerrain, ref mayLeaveBrush, out floor, trace, inclinedTarget))
                return true;
        }
        return !belowTerrain && mayLeaveBrush && TryCrossPlacedFloorFaces(npc, cellX, cellY, from, to, out floor, trace);
    }

    private CryWorldObjectIndex GetGroundBrushIndex(WorldCell cell, ObjectsFile objects) =>
        _groundBrushes.GetValue(objects, data => new CryWorldObjectIndex(
            CryWorldObjectIndex.ReadBrushInstances(data, cell.CellX, cell.CellY)));

    private bool TryFollowFloorInCell(WorldCell cell, Vector3 from, Vector3 to, bool belowTerrain,
        ref bool mayLeaveBrush, out float floor, NpcStepSurfaceTrace trace, Vector3? inclinedTarget)
    {
        floor = default;
        if (cell?.LoadedObjectDat is not { HasUnparsedObjects: false } objects)
            return false;

        try
        {
            var brushes = GetGroundBrushIndex(cell, objects);
            trace?.Observe(NoSharedBrush, cell);
            // Both feet must fit the same placement. Query only the previous foot's bucket.
            foreach (var brush in brushes.Query(from - Vector3.UnitZ * StaticFloorPrecision, from + Vector3.UnitZ * StaticFloorPrecision))
            {
                if (!brush.ModelUri.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (to.X < brush.Min.X || to.X > brush.Max.X || to.Y < brush.Min.Y || to.Y > brush.Max.Y)
                {
                    mayLeaveBrush = true;
                    trace?.Observe(DestinationOutsideBrush, cell, brush);
                    continue;
                }

                // A distant pursuit needs no geometry reads when its target lies outside this placement.
                if (inclinedTarget is { } target && (target.X < brush.Min.X || target.X > brush.Max.X ||
                    target.Y < brush.Min.Y || target.Y > brush.Max.Y))
                {
                    trace?.Observe(DestinationOutsideBrush, cell, brush);
                    continue;
                }

                var asset = TryReadBrush(brush);
                if (asset == null)
                {
                    trace?.Observe(GeometryUnavailable, cell, brush);
                    continue;
                }
                trace?.Observe(NoStaticSolidMesh, cell, brush);
                if (asset.HasAnimatedCollision || asset.PoseRequirements.Any(pose => pose.AffectsCollision))
                    continue;

                for (var index = 0; index < asset.Parts.Count; index++)
                {
                    var part = asset.Parts[index];
                    if (part.PhysicsType != 0x1000 || part.Shape is not CryTriangleMesh mesh)
                        continue;
                    trace?.Observe(NoStartFace, cell, brush, index);
                    if (!TryFloorHit(part, brush.Transform, from, belowTerrain, out var first))
                        continue;
                    mayLeaveBrush = true;
                    trace?.Observe(NoEndFace, cell, brush, index, first.TriangleIndex);
                    if (!TryFloorHit(part, brush.Transform, to, belowTerrain, out var last))
                        continue;

                    // A height difference along a cave slope can exceed the tolerance for a level step.
                    // A single convex face supporting both feet and the target proves the full segment.
                    if (inclinedTarget is { } destination && (first.Normal.Z == 1f || first.TriangleIndex != last.TriangleIndex ||
                        !TryFloorHit(part, brush.Transform, destination, belowTerrain, out var end) || end.TriangleIndex != first.TriangleIndex))
                    {
                        trace?.Observe(TargetOutsideInclinedFace, cell, brush, index, first.TriangleIndex, last.TriangleIndex);
                        continue;
                    }
                    if (inclinedTarget is { } endFoot &&
                        (HasOtherFootSupport(from, brush, index, first.TriangleIndex) ||
                         HasOtherFootSupport(endFoot, brush, index, first.TriangleIndex)))
                    {
                        trace?.Observe(AmbiguousInclinedFoot, cell, brush, index, first.TriangleIndex, last.TriangleIndex);
                        continue;
                    }
                    if (!CanFollowFloorFaces(mesh,
                            part.Transform * brush.Transform, first.TriangleIndex, last.TriangleIndex, from, to, belowTerrain))
                    {
                        trace?.Observe(FacesDisconnectedAlongStep, cell, brush, index, first.TriangleIndex, last.TriangleIndex);
                        continue;
                    }
                    trace?.Observe(ContinuousStaticFloor, cell, brush, index, first.TriangleIndex, last.TriangleIndex);
                    floor = last.Position.Z;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            trace?.Observe(GeometryUnavailable, cell);
            Logger.Debug(ex, "Could not read static floor geometry at {0} in {1}", from, worldTemplate.Name);
        }
        return false;
    }

    // The ordinary level-step ray takes the first upward hit within the foot tolerance.
    // For a change in elevation, a second solid inside that band leaves the intended layer ambiguous.
    private bool HasOtherFootSupport(Vector3 foot, CryWorldObjectInstance support, int partIndex, int face)
    {
        var (cellX, cellY) = foot.ToCellIndex();
        var margin = Vector3.UnitZ * StaticFloorPrecision;
        for (var y = cellY - 1; y <= cellY + 1; y++)
        for (var x = cellX - 1; x <= cellX + 1; x++)
        {
            var cell = worldTemplate.GetCell(x, y);
            if (cell?.LoadedObjectDat is not { HasUnparsedObjects: false } objects)
                continue;
            foreach (var brush in GetGroundBrushIndex(cell, objects).Query(foot - margin, foot + margin))
            {
                if (!brush.ModelUri.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
                    continue;
                var asset = TryReadBrush(brush);
                if (asset == null || asset.HasAnimatedCollision || asset.PoseRequirements.Any(pose => pose.AffectsCollision))
                    continue;
                for (var i = 0; i < asset.Parts.Count; i++)
                {
                    var part = asset.Parts[i];
                    if (part.PhysicsType != 0x1000)
                        continue;
                    var exclude = ReferenceEquals(brush, support) && i == partIndex ? face : -1;
                    if (CryGeometryQueries.Raycast(part, brush.Transform, foot + margin, -Vector3.UnitZ,
                            StaticFloorPrecision * 2, true, out _, exclude))
                        return true;
                }
            }
        }
        return false;
    }

    private CryGeometryAsset TryReadBrush(CryWorldObjectInstance brush)
    {
        try
        {
            return GroundBrushGeometry(brush);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            Logger.Debug(ex, "Could not read static floor candidate {0}", brush.ModelUri);
            return null;
        }
    }

    // The short ray tolerates floating-point roundoff at the accepted foot height. It cannot select another level.
    private static bool TryFloorHit(CryGeometryPart part, Matrix4x4 transform, Vector3 foot,
        bool belowTerrain, out CryRayHit hit)
    {
        if (!CryGeometryQueries.Raycast(part, transform, foot + Vector3.UnitZ * StaticFloorPrecision,
                -Vector3.UnitZ, StaticFloorPrecision * 2, true, out hit))
            return false;
        return hit.Normal.Z == 1f || belowTerrain && IsGentlyInclined(hit.Normal);
    }

    internal static bool FitsCaveFloorGrade(Vector3 from, Vector3 destination)
    {
        var offset = destination - from;
        var rise = offset.Z * CaveFloorReferenceSpan;
        return rise * rise <= StaticFloorPrecision * StaticFloorPrecision * (offset.X * offset.X + offset.Y * offset.Y);
    }

    private static bool IsGentlyInclined(Vector3 normal)
    {
        // Allow at most one centimetre of rise over half a metre in any direction.
        // A short last step along a steep wall must not make the wall qualify as a floor.
        var horizontalNormal = new Vector2(normal.X, normal.Y);
        return normal.Z > 0 && CaveFloorReferenceSpan * horizontalNormal.Length() <= StaticFloorPrecision * normal.Z;
    }

    // Adjacent faces cover most steps. Build the per-mesh vertex index only for a longer chain.
    private static readonly ConditionalWeakTable<CryTriangleMesh, ILookup<ushort, int>> FacesByVertex = new();

    private static bool CanFollowFloorFaces(CryTriangleMesh mesh, Matrix4x4 transform, int first, int last,
        Vector3 from, Vector3 to, bool belowTerrain)
    {
        if (first == last || CrossesSharedEdge(mesh, transform, first, last, from, to))
            return true;

        var faces = FacesByVertex.GetValue(mesh, static geometry =>
            Enumerable.Range(0, geometry.Indices.Length).ToLookup(i => geometry.Indices[i], i => i / 3));
        Matrix4x4.Invert(transform, out var inverse); // The two successful raycasts already checked this transform.
        var normalTransform = Matrix4x4.Transpose(inverse);
        var visited = new HashSet<int> { first };
        var pending = new Stack<int>();
        pending.Push(first);
        while (pending.TryPop(out var current))
        {
            var indices = mesh.Indices.AsSpan(current * 3, 3);
            for (var edge = 0; edge < 3; edge++)
            {
                var a = indices[edge];
                var b = indices[(edge + 1) % 3];
                if (!EdgeCrossesStep(mesh, transform, a, b, from, to))
                    continue;

                foreach (var neighbor in faces[a])
                {
                    if (visited.Contains(neighbor))
                        continue;
                    var next = mesh.Indices.AsSpan(neighbor * 3, 3);
                    if (!next.Contains(b))
                        continue;
                    if (neighbor == last)
                        return true; // Its upward support at the destination was already measured.

                    visited.Add(neighbor);
                    var p = mesh.Vertices[next[0]];
                    var q = mesh.Vertices[next[1]];
                    var r = mesh.Vertices[next[2]];
                    var normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.Cross(q - p, r - p), normalTransform));
                    var vertex = Vector3.Transform(p, transform);
                    if (belowTerrain && normal.Z != 1f)
                    {
                        // Bound the plane along the step. Other corners of the same cave face can
                        // lie outside the height band. The signed plane distance is normal.Z times the vertical gap.
                        var band = StaticFloorPrecision * normal.Z;
                        if (IsGentlyInclined(normal) && MathF.Abs(Vector3.Dot(normal, from - vertex)) <= band &&
                            MathF.Abs(Vector3.Dot(normal, to - vertex)) <= band)
                            pending.Push(neighbor);
                    }
                    else if (normal.Z == 1f &&
                             MathF.Abs(vertex.Z - from.Z) <= StaticFloorPrecision &&
                             MathF.Abs(Vector3.Transform(q, transform).Z - from.Z) <= StaticFloorPrecision &&
                             MathF.Abs(Vector3.Transform(r, transform).Z - from.Z) <= StaticFloorPrecision)
                        pending.Push(neighbor);
                }
            }
        }
        return false;
    }

    private static bool EdgeCrossesStep(CryTriangleMesh mesh, Matrix4x4 transform, ushort a, ushort b,
        Vector3 from, Vector3 to)
    {
        var start = Vector3.Transform(mesh.Vertices[a], transform);
        var end = Vector3.Transform(mesh.Vertices[b], transform);
        // Collinear edges can lie beyond either foot. The straight step must touch the finite edge.
        return Math.Max(start.X, end.X) >= Math.Min(from.X, to.X) && Math.Min(start.X, end.X) <= Math.Max(from.X, to.X) &&
               Math.Max(start.Y, end.Y) >= Math.Min(from.Y, to.Y) && Math.Min(start.Y, end.Y) <= Math.Max(from.Y, to.Y) &&
               Side(from, to, start) * Side(from, to, end) <= 0f && Side(start, end, from) * Side(start, end, to) <= 0f;
    }

    private static bool CrossesSharedEdge(CryTriangleMesh mesh, Matrix4x4 transform, int first, int last,
        Vector3 from, Vector3 to)
    {
        var firstIndices = mesh.Indices.AsSpan(first * 3, 3);
        var lastIndices = mesh.Indices.AsSpan(last * 3, 3);
        for (var i = 0; i < firstIndices.Length; i++)
        {
            var a = firstIndices[i];
            var b = firstIndices[(i + 1) % 3];
            if (!lastIndices.Contains(a) || !lastIndices.Contains(b))
                continue;

            return EdgeCrossesStep(mesh, transform, a, b, from, to);
        }
        return false;
    }

    private static float Side(Vector3 a, Vector3 b, Vector3 point) =>
        (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
}
