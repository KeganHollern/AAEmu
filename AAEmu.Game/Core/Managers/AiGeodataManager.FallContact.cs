using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    private readonly ConditionalWeakTable<CryWorldObjectInstance, BrushFaces> _fallFaces = new();
    private readonly record struct LevelFace(Vector3 A, Vector3 B, Vector3 C)
    {
        public bool Covers(Vector3 foot) => SideOfEdge(A, B, foot) >= 0 && SideOfEdge(B, C, foot) >= 0 && SideOfEdge(C, A, foot) >= 0;
    }
    private readonly record struct InclinedFace(LevelFace Face, Vector2 Gradient);
    private sealed record InclinedGroup(CryBounds Bounds, InclinedFace[] Faces);
    private sealed record BrushFaces(LevelFace[] Level, InclinedGroup[] Inclined);

    // All r208022 playable models use a minimum fall angle of 50 degrees.
    private static readonly float PlayerFallNormalZ = MathF.Cos(50f * MathF.PI / 180f);

    // Use only already loaded static placements. A brush can overhang its own world cell.
    internal bool HasBrushFallSupport(Vector3 foot, bool playerInclines = false)
    {
        if (!float.IsFinite(foot.X) || !float.IsFinite(foot.Y) || !float.IsFinite(foot.Z))
            return false;
        var (cellX, cellY) = foot.ToCellIndex();
        if (worldTemplate.Cells is not { } cells || (uint)cellX >= cells.GetLength(0) || (uint)cellY >= cells.GetLength(1))
            return false;
        if (HasContactInCell(worldTemplate.GetCell(cellX, cellY), foot, playerInclines))
            return true;
        for (var y = cellY - 1; y <= cellY + 1; y++)
        for (var x = cellX - 1; x <= cellX + 1; x++)
        {
            if ((x != cellX || y != cellY) && HasContactInCell(worldTemplate.GetCell(x, y), foot, playerInclines))
                return true;
        }
        return false;
    }

    private bool HasContactInCell(WorldCell cell, Vector3 foot, bool playerInclines)
    {
        if (cell?.LoadedObjectDat is not { HasUnparsedObjects: false } objects)
            return false;
        try
        {
            return GetGroundBrushIndex(cell, objects).AnyAt(foot, FallMovement.PositionTolerance, (Ground: this, Player: playerInclines),
                static (state, brush, position) => state.Ground.HasContactOnBrush(brush, position, state.Player));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or OverflowException)
        {
            Logger.Debug(ex, "Could not measure the static floor in cell {0},{1}", cell.CellX, cell.CellY);
            return false;
        }
    }

    private bool HasContactOnBrush(CryWorldObjectInstance brush, Vector3 foot, bool playerInclines)
    {
        if (!_fallFaces.TryGetValue(brush, out var faces))
        {
            if (!brush.ModelUri.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
                return false;
            var asset = TryReadBrush(brush);
            // A failed read supplies no geometry. A later successful read can still establish contact.
            if (asset == null || asset.Parts.Count == 0)
                return false;
            faces = _fallFaces.GetOrAdd(brush, static (placement, model) =>
            {
                var inclined = new List<InclinedFace>();
                var level = ReadLevelFaces(placement.Transform, model, inclined);
                return new BrushFaces(level, GroupInclines(inclined));
            }, asset);
        }
        var local = foot - brush.Transform.Translation;
        if (TouchesLevelFace(faces.Level, local))
            return true;
        if (!playerInclines)
            return false;
        var horizontalRounding = new Vector2(TruncatedSpan(foot.X), TruncatedSpan(foot.Y));
        foreach (var group in faces.Inclined)
        {
            if (!group.Bounds.Contains(local))
                continue;
            foreach (ref readonly var slope in group.Faces.AsSpan())
            {
                var delta = local - slope.Face.A;
                var dz = delta.Z - (delta.X * slope.Gradient.X + delta.Y * slope.Gradient.Y);
                if (TouchesIncline(slope, local, horizontalRounding, dz))
                    return true;
            }
        }
        return false;
    }

    // The stock movement position truncates positive X and Y to 8/4096 m. At large
    // world coordinates, adjacent floats can be as widely spaced as that whole interval.
    private static float TruncatedSpan(float coordinate)
    {
        const float step = 1f / 512f;
        var cell = coordinate / step;
        if (coordinate < 0 || cell != MathF.Truncate(cell))
            return 0;
        return MathF.Max(0, MathF.BitDecrement(coordinate + step) - coordinate);
    }

    private static bool TouchesIncline(in InclinedFace slope, Vector3 local, Vector2 rounding, float dz)
    {
        var missing = MathF.Abs(dz) - FallMovement.PositionTolerance;
        if (missing <= 0)
            return slope.Face.Covers(local);

        // The possible X/Y values form a rectangle. The required height cuts that rectangle
        // along a line. Only a point on that line inside this triangle can explain the gap.
        var gradient = slope.Gradient;
        var dx = dz * gradient.X > 0 ? rounding.X : 0;
        var dy = dz * gradient.Y > 0 ? rounding.Y : 0;
        var available = Math.Abs((double)gradient.X * dx + (double)gradient.Y * dy);
        if (available < missing || !slope.Face.Covers(local))
            return false;
        var x = dx * (missing / available);
        var y = dy * (missing / available);
        var directionX = -(double)gradient.Y;
        var directionY = (double)gradient.X;
        var min = double.NegativeInfinity;
        var max = double.PositiveInfinity;
        return Clip(x, directionX) && Clip(rounding.X - x, -directionX) &&
               Clip(y, directionY) && Clip(rounding.Y - y, -directionY) &&
               InsideEdge(slope.Face.A, slope.Face.B) && InsideEdge(slope.Face.B, slope.Face.C) &&
               InsideEdge(slope.Face.C, slope.Face.A);

        bool InsideEdge(Vector3 a, Vector3 b)
        {
            var edgeX = (double)b.X - a.X;
            var edgeY = (double)b.Y - a.Y;
            return Clip(edgeX * (local.Y - (double)a.Y + y) - edgeY * (local.X - (double)a.X + x),
                edgeX * directionY - edgeY * directionX);
        }

        // Keep the interval where value + t * rate >= 0.
        bool Clip(double value, double rate)
        {
            if (rate == 0)
                return value >= 0;
            var boundary = -value / rate;
            if (rate > 0)
                min = Math.Max(min, boundary);
            else
                max = Math.Min(max, boundary);
            return min <= max;
        }
    }

    // Dense rock meshes can contribute hundreds of faces in a single broad-phase placement.
    private static InclinedGroup[] GroupInclines(List<InclinedFace> slopes)
    {
        if (slopes.Count == 0)
            return [];
        slopes.Sort(static (a, b) => a.Face.A.Z.CompareTo(b.Face.A.Z));
        return slopes.Chunk(16).Select(group =>
        {
            var min = group[0].Face.A;
            var max = min;
            foreach (var slope in group)
            {
                var (a, b, c) = slope.Face;
                min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
                max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            }
            var margin = Vector3.UnitZ * FallMovement.PositionTolerance;
            return new InclinedGroup(new(min - margin, max + margin), group);
        }).ToArray();
    }

    private static bool TouchesLevelFace(LevelFace[] faces, Vector3 local)
    {
        foreach (var face in faces)
        {
            if (MathF.Abs(local.Z - face.A.Z) <= FallMovement.PositionTolerance && face.Covers(local))
                return true;
        }
        return false;
    }

    private static LevelFace[] ReadLevelFaces(Matrix4x4 placement, CryGeometryAsset asset, List<InclinedFace> inclined = null)
    {
        if (asset.HasAnimatedCollision || asset.PoseRequirements.Any(pose => pose.AffectsCollision))
            return [];

        var offset = placement;
        offset.Translation = Vector3.Zero;
        var result = new List<LevelFace>();
        foreach (var part in asset.Parts)
        {
            if (part.PhysicsType != 0x1000)
                continue;
            var transform = part.Transform * offset;
            if (part.Shape is CryBox box && inclined != null)
            {
                AddLevelBoxFace(result, box, transform);
                AddInclinedBoxFaces(inclined, box, transform);
            }
            if (part.Shape is not CryTriangleMesh mesh)
                continue;
            var mirrored = transform.GetDeterminant() < 0;
            for (var n = 0; n < mesh.Indices.Length; n += 3)
            {
                var a = Vector3.Transform(mesh.Vertices[mesh.Indices[n]], transform);
                var b = Vector3.Transform(mesh.Vertices[mesh.Indices[n + 1]], transform);
                var c = Vector3.Transform(mesh.Vertices[mesh.Indices[n + 2]], transform);
                // Resolve height at world precision. The horizontal projection stays in the
                // placement's local frame so a real edge or a gap between triangles stays open.
                var z = placement.Translation.Z;
                if (mirrored)
                    (b, c) = (c, b);
                var level = a.Z + z == b.Z + z && a.Z + z == c.Z + z;
                if (!level && inclined == null)
                    continue;
                var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                if (level)
                {
                    if (normal.Z == 1f)
                        result.Add(new LevelFace(a, b, c));
                }
                else if (inclined != null && normal.Z > PlayerFallNormalZ)
                    inclined.Add(new InclinedFace(new(a, b, c), new(-normal.X / normal.Z, -normal.Y / normal.Z)));
            }
        }
        return result.ToArray();
    }

    // For each pair of opposite box faces, only the upward face can support a player.
    private static void AddInclinedBoxFaces(List<InclinedFace> faces, CryBox box, Matrix4x4 transform)
    {
        var center = Vector3.Transform(box.Center, transform);
        var axes = box.Orientation * transform;
        var x = Vector3.TransformNormal(Vector3.UnitX * box.HalfSize.X, axes);
        var y = Vector3.TransformNormal(Vector3.UnitY * box.HalfSize.Y, axes);
        var z = Vector3.TransformNormal(Vector3.UnitZ * box.HalfSize.Z, axes);
        AddSide(x, y, z);
        AddSide(y, z, x);
        AddSide(z, x, y);

        void AddSide(Vector3 u, Vector3 v, Vector3 axis)
        {
            if (u.Z == 0 && v.Z == 0)
                return;
            var normal = Vector3.Normalize(Vector3.Cross(u, v));
            if (normal.Z < 0)
            {
                (u, v) = (v, u);
                normal = -normal;
            }
            if (!(normal.Z > PlayerFallNormalZ))
                return;
            var middle = center + (Vector3.Dot(normal, axis) > 0 ? axis : -axis);
            var gradient = new Vector2(-normal.X / normal.Z, -normal.Y / normal.Z);
            faces.Add(new InclinedFace(new(middle - u - v, middle + u - v, middle + u + v), gradient));
            faces.Add(new InclinedFace(new(middle - u - v, middle + u + v, middle - u + v), gradient));
        }
    }
}
