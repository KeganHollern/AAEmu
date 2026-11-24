using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    internal const float StaticFloorPrecision = 0.01f;
    private readonly ConditionalWeakTable<ObjectsFile, CryWorldObjectIndex> _groundBrushes = new();
    internal Func<CryWorldObjectInstance, CryGeometryAsset> GroundBrushGeometry { get; init; } =
        new CryWorldGeometryResolver(new CryGeometryResolver(ClientFileManager.GetFileStream).Load).Load;

    // A horizontal NPC step can leave a waypoint's region of influence while still on its solid deck.
    // Only a continuous piece of the same placed mesh can supersede the terrain result for this step.
    internal bool TryFollowStaticFloor(Vector3 from, Vector3 to, out float floor)
    {
        floor = default;
        var cellIndex = from.ToCellIndex();
        if (cellIndex != to.ToCellIndex() || !worldTemplate.TryGetHeight(from.X, from.Y, out var terrain) ||
            from.Z <= terrain + StaticFloorPrecision)
            return false;

        var cell = worldTemplate.GetCell(cellIndex.Item1, cellIndex.Item2);
        if (cell?.LoadedObjectDat is not { HasUnparsedObjects: false } objects)
            return false;

        try
        {
            var brushes = _groundBrushes.GetValue(objects, data => new CryWorldObjectIndex(
                CryWorldObjectIndex.ReadBrushInstances(data, cell.CellX, cell.CellY)));
            // Both feet must fit the same placement. Query only the previous foot's bucket.
            foreach (var brush in brushes.Query(from - Vector3.UnitZ * StaticFloorPrecision, from + Vector3.UnitZ * StaticFloorPrecision))
            {
                if (to.X < brush.Min.X || to.X > brush.Max.X || to.Y < brush.Min.Y || to.Y > brush.Max.Y ||
                    !brush.ModelUri.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
                    continue;

                var asset = TryReadBrush(brush);
                if (asset == null || asset.HasAnimatedCollision || asset.PoseRequirements.Any(pose => pose.AffectsCollision))
                    continue;

                foreach (var part in asset.Parts)
                {
                    if (part.PhysicsType != 0x1000 || part.Shape is not CryTriangleMesh mesh ||
                        !TryFloorHit(part, brush.Transform, from, out var first) ||
                        !TryFloorHit(part, brush.Transform, to, out var last))
                        continue;

                    if (first.TriangleIndex != last.TriangleIndex && !CrossesSharedEdge(mesh,
                            part.Transform * brush.Transform, first.TriangleIndex, last.TriangleIndex, from, to))
                        continue;

                    floor = last.Position.Z;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            Logger.Debug(ex, "Could not read static floor geometry at {0} in {1}", from, worldTemplate.Name);
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
    private static bool TryFloorHit(CryGeometryPart part, Matrix4x4 transform, Vector3 foot, out CryRayHit hit) =>
        CryGeometryQueries.Raycast(part, transform, foot + Vector3.UnitZ * StaticFloorPrecision,
            -Vector3.UnitZ, StaticFloorPrecision * 2, true, out hit) && hit.Normal.Z == 1f;

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

            var edgeStart = Vector3.Transform(mesh.Vertices[a], transform);
            var edgeEnd = Vector3.Transform(mesh.Vertices[b], transform);
            return Side(from, to, edgeStart) * Side(from, to, edgeEnd) <= 0f &&
                   Side(edgeStart, edgeEnd, from) * Side(edgeStart, edgeEnd, to) <= 0f;
        }
        return false;
    }

    private static float Side(Vector3 a, Vector3 b, Vector3 point) =>
        (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
}
