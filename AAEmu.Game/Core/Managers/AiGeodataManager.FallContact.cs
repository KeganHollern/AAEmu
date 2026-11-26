using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers;

public partial class AiGeoDataManager
{
    private readonly ConditionalWeakTable<CryWorldObjectInstance, LevelFace[]> _fallFaces = new();
    private readonly record struct LevelFace(Vector3 A, Vector3 B, Vector3 C);

    // The observer can confirm contact on a level, upward solid face at the reported feet.
    // Use only already loaded static placements. A brush can overhang its own world cell.
    internal bool HasFlatFallSupport(Vector3 foot)
    {
        if (!float.IsFinite(foot.X) || !float.IsFinite(foot.Y) || !float.IsFinite(foot.Z))
            return false;
        var (cellX, cellY) = foot.ToCellIndex();
        if (worldTemplate.Cells is not { } cells || (uint)cellX >= cells.GetLength(0) || (uint)cellY >= cells.GetLength(1))
            return false;
        if (HasContactInCell(worldTemplate.GetCell(cellX, cellY), foot))
            return true;
        for (var y = cellY - 1; y <= cellY + 1; y++)
        for (var x = cellX - 1; x <= cellX + 1; x++)
        {
            if ((x != cellX || y != cellY) && HasContactInCell(worldTemplate.GetCell(x, y), foot))
                return true;
        }
        return false;
    }

    private bool HasContactInCell(WorldCell cell, Vector3 foot)
    {
        if (cell?.LoadedObjectDat is not { HasUnparsedObjects: false } objects)
            return false;
        try
        {
            return GetGroundBrushIndex(cell, objects).AnyAt(foot, FallMovement.PositionTolerance, this,
                static (ground, brush, position) => ground.HasContactOnBrush(brush, position));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or OverflowException)
        {
            Logger.Debug(ex, "Could not measure the static floor in cell {0},{1}", cell.CellX, cell.CellY);
            return false;
        }
    }

    private bool HasContactOnBrush(CryWorldObjectInstance brush, Vector3 foot)
    {
        if (!_fallFaces.TryGetValue(brush, out var faces))
        {
            if (!brush.ModelUri.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase))
                return false;
            var asset = TryReadBrush(brush);
            // A failed read supplies no geometry. A later successful read can still establish contact.
            if (asset == null || asset.Parts.Count == 0)
                return false;
            faces = _fallFaces.GetOrAdd(brush, static (placement, model) => ReadLevelFaces(placement, model), asset);
        }
        var local = foot - brush.Transform.Translation;
        foreach (var face in faces)
        {
            if (MathF.Abs(local.Z - face.A.Z) <= FallMovement.PositionTolerance &&
                SideOfEdge(face.A, face.B, local) >= 0 && SideOfEdge(face.B, face.C, local) >= 0 &&
                SideOfEdge(face.C, face.A, local) >= 0)
                return true;
        }
        return false;
    }

    private static LevelFace[] ReadLevelFaces(CryWorldObjectInstance brush, CryGeometryAsset asset)
    {
        if (asset.HasAnimatedCollision || asset.PoseRequirements.Any(pose => pose.AffectsCollision))
            return [];

        var offset = brush.Transform;
        offset.Translation = Vector3.Zero;
        var result = new List<LevelFace>();
        foreach (var part in asset.Parts)
        {
            if (part.PhysicsType != 0x1000 || part.Shape is not CryTriangleMesh mesh)
                continue;
            var transform = part.Transform * offset;
            var mirrored = transform.GetDeterminant() < 0;
            for (var n = 0; n < mesh.Indices.Length; n += 3)
            {
                var a = Vector3.Transform(mesh.Vertices[mesh.Indices[n]], transform);
                var b = Vector3.Transform(mesh.Vertices[mesh.Indices[n + 1]], transform);
                var c = Vector3.Transform(mesh.Vertices[mesh.Indices[n + 2]], transform);
                // Resolve height at world precision. The horizontal projection stays in the
                // placement's local frame so a real edge or a gap between triangles stays open.
                var z = brush.Transform.Translation.Z;
                if (a.Z + z != b.Z + z || a.Z + z != c.Z + z)
                    continue;
                if (mirrored)
                    (b, c) = (c, b);
                if (Vector3.Normalize(Vector3.Cross(b - a, c - a)).Z == 1f)
                    result.Add(new LevelFace(a, b, c));
            }
        }
        return result.ToArray();
    }
}
