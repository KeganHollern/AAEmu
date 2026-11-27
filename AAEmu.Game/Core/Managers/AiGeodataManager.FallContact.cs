using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    private readonly ConditionalWeakTable<CryWorldObjectInstance, FallFaces> _fallFaces = new();
    private readonly record struct LevelFace(Vector3 A, Vector3 B, Vector3 C)
    {
        public bool Covers(Vector3 foot) => SideOfEdge(A, B, foot) >= 0 && SideOfEdge(B, C, foot) >= 0 && SideOfEdge(C, A, foot) >= 0;
    }
    private readonly record struct InclinedFace(LevelFace Face, Vector2 Gradient);
    private sealed record InclinedGroup(CryBounds Bounds, InclinedFace[] Faces);
    private readonly record struct CylinderCap(Vector3 Center, Vector3 LocalX, Vector3 LocalY, float RadiusSquared, Vector2 Gradient)
    {
        public bool Touches(Vector3 foot, Vector2 rounding)
        {
            var delta = foot - Center;
            var radius = delta.X * LocalX + delta.Y * LocalY;
            if (!(radius.LengthSquared() <= RadiusSquared))
                return false;
            var dz = delta.Z - (delta.X * Gradient.X + delta.Y * Gradient.Y);
            if (MathF.Abs(dz) <= FallMovement.PositionTolerance)
                return true;
            if (Gradient == Vector2.Zero || !RoundingLine.TryCreate(Gradient, rounding, dz, out var line))
                return false;

            // The horizontal footprint of the disk is an ellipse. The closest point on the
            // allowed height line, in model coordinates, must still lie inside the disk.
            var origin = radius + (float)line.X * LocalX + (float)line.Y * LocalY;
            var direction = (float)line.DirectionX * LocalX + (float)line.DirectionY * LocalY;
            var parameter = Math.Clamp(-(double)Vector3.Dot(origin, direction) / direction.LengthSquared(), line.Min, line.Max);
            return (origin + (float)parameter * direction).LengthSquared() <= RadiusSquared;
        }
    }
    private readonly record struct SphereSurface(CryGeometryPart Part, CryBounds Bounds)
    {
        public bool Touches(Vector3 foot, Vector2 rounding)
        {
            const float tolerance = FallMovement.PositionTolerance;
            var sample = new CryBounds(foot - Vector3.UnitZ * tolerance, foot + new Vector3(rounding, tolerance));
            if (!Bounds.Intersects(sample))
                return false;

            // Sample the decoded foot and the other ends of the stock horizontal rounding interval.
            // Each accepted point must touch the actual upward, walkable surface within the Z tolerance.
            return TouchesAt(foot) ||
                   (rounding.X > 0 && TouchesAt(foot + Vector3.UnitX * rounding.X)) ||
                   (rounding.Y > 0 && TouchesAt(foot + Vector3.UnitY * rounding.Y)) ||
                   (rounding.X > 0 && rounding.Y > 0 && TouchesAt(foot + new Vector3(rounding, 0)));
        }

        private bool TouchesAt(Vector3 foot) =>
            CryGeometryQueries.Raycast(Part, Matrix4x4.Identity, foot + Vector3.UnitZ * FallMovement.PositionTolerance,
                -Vector3.UnitZ, 2 * FallMovement.PositionTolerance, true, out var hit) && hit.Normal.Z > PlayerFallNormalZ;
    }

    private sealed record FallFaces(LevelFace[] Level, InclinedGroup[] Inclined, CylinderCap[] Caps)
    {
        public SphereSurface[] Spheres { get; init; } = [];
    }

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
                ReadFallFaces(placement.Transform, model, includeBrushPrimitives: true), asset);
        }
        return TouchesFallFace(faces, foot, foot - brush.Transform.Translation, playerInclines);
    }

    private static bool TouchesFallFace(FallFaces faces, Vector3 foot, Vector3 local, bool playerInclines)
    {
        if (TouchesLevelFace(faces.Level, local))
            return true;
        var horizontalRounding = playerInclines ? new Vector2(TruncatedSpan(foot.X), TruncatedSpan(foot.Y)) : Vector2.Zero;
        foreach (ref readonly var cap in faces.Caps.AsSpan())
            if ((playerInclines || cap.Gradient == Vector2.Zero) && cap.Touches(local, horizontalRounding))
                return true;
        if (!playerInclines)
            return false;
        foreach (ref readonly var sphere in faces.Spheres.AsSpan())
            if (sphere.Touches(local, horizontalRounding))
                return true;
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
        if (MathF.Abs(dz) <= FallMovement.PositionTolerance)
            return slope.Face.Covers(local);
        if (!RoundingLine.TryCreate(slope.Gradient, rounding, dz, out var line) || !slope.Face.Covers(local))
            return false;
        return InsideEdge(slope.Face.A, slope.Face.B) && InsideEdge(slope.Face.B, slope.Face.C) &&
               InsideEdge(slope.Face.C, slope.Face.A);

        bool InsideEdge(Vector3 a, Vector3 b)
        {
            var edgeX = (double)b.X - a.X;
            var edgeY = (double)b.Y - a.Y;
            return line.Clip(edgeX * (local.Y - (double)a.Y + line.Y) - edgeY * (local.X - (double)a.X + line.X),
                edgeX * line.DirectionY - edgeY * line.DirectionX);
        }
    }

    // The possible X/Y positions form a rectangle. The missing height cuts that rectangle
    // along a line. Both triangles and disks must cover some point on the same line.
    private struct RoundingLine(double x, double y, double directionX, double directionY)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
        public double DirectionX { get; } = directionX;
        public double DirectionY { get; } = directionY;
        public double Min { get; private set; } = double.NegativeInfinity;
        public double Max { get; private set; } = double.PositiveInfinity;

        public static bool TryCreate(Vector2 gradient, Vector2 rounding, float dz, out RoundingLine line)
        {
            var missing = MathF.Abs(dz) - FallMovement.PositionTolerance;
            var dx = dz * gradient.X > 0 ? rounding.X : 0;
            var dy = dz * gradient.Y > 0 ? rounding.Y : 0;
            var available = Math.Abs((double)gradient.X * dx + (double)gradient.Y * dy);
            if (available < missing)
            {
                line = default;
                return false;
            }
            var x = dx * (missing / available);
            var y = dy * (missing / available);
            line = new RoundingLine(x, y, -gradient.Y, gradient.X);
            return line.Clip(x, line.DirectionX) && line.Clip(rounding.X - x, -line.DirectionX) &&
                   line.Clip(y, line.DirectionY) && line.Clip(rounding.Y - y, -line.DirectionY);
        }

        // Keep the interval where value + t * rate >= 0.
        public bool Clip(double value, double rate)
        {
            if (rate == 0)
                return value >= 0;
            var boundary = -value / rate;
            if (rate > 0)
                Min = Math.Max(Min, boundary);
            else
                Max = Math.Min(Max, boundary);
            return Min <= Max;
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

    private static FallFaces ReadFallFaces(Matrix4x4 placement, CryGeometryAsset asset, bool includeBrushPrimitives)
    {
        var inclined = new List<InclinedFace>();
        var caps = includeBrushPrimitives ? new List<CylinderCap>() : null;
        var spheres = includeBrushPrimitives ? new List<SphereSurface>() : null;
        var level = ReadLevelFaces(placement, asset, inclined, caps, spheres);
        return new FallFaces(level, GroupInclines(inclined), caps?.ToArray() ?? []) { Spheres = spheres?.ToArray() ?? [] };
    }

    private static LevelFace[] ReadLevelFaces(Matrix4x4 placement, CryGeometryAsset asset,
        List<InclinedFace> inclined = null, List<CylinderCap> brushCaps = null, List<SphereSurface> brushSpheres = null)
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
            if (brushSpheres != null && part.Shape is CrySphere { Radius: > 0 } && Matrix4x4.Invert(transform, out _))
            {
                var sphere = part with { Transform = transform };
                brushSpheres.Add(new SphereSurface(sphere, CryGeometryQueries.GetBounds(sphere, Matrix4x4.Identity)));
            }
            if (brushCaps != null && part.Shape is CryCylinder { IsCapsule: false } cylinder)
                AddCylinderCap(brushCaps, cylinder, transform);
            if (brushCaps != null && part.Shape is CryBox box && inclined != null)
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

    private static void AddCylinderCap(List<CylinderCap> caps, CryCylinder cylinder, Matrix4x4 transform)
    {
        if (cylinder.Radius <= 0 || cylinder.HalfHeight <= 0 || !Matrix4x4.Invert(transform, out var inverse))
            return;
        var normal = Vector3.Normalize(Vector3.TransformNormal(cylinder.Axis, Matrix4x4.Transpose(inverse)));
        if (!(MathF.Abs(normal.Z) > PlayerFallNormalZ))
            return;
        var top = cylinder.Center + cylinder.Axis * MathF.CopySign(cylinder.HalfHeight, normal.Z);
        var gradient = new Vector2(-normal.X / normal.Z, -normal.Y / normal.Z);
        // Each horizontal metre also moves along the cap plane before conversion to model coordinates.
        caps.Add(new CylinderCap(Vector3.Transform(top, transform),
            Vector3.TransformNormal(new Vector3(1, 0, gradient.X), inverse),
            Vector3.TransformNormal(new Vector3(0, 1, gradient.Y), inverse), cylinder.Radius * cylinder.Radius, gradient));
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
