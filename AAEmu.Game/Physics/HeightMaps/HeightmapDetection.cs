using AAEmu.Game.Models.Game.Units;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace AAEmu.Game.Physics.HeightMaps;

public sealed class HeightmapDetection(Jitter2.World world, HeightmapTester shape) : IBroadPhaseFilter
{
    private readonly Heightmap _heightmap = shape.Heightmap;
    private readonly ulong _minIndex = Jitter2.World.RequestId(shape.Heightmap.TriangleCount).Item1;

    public bool Filter(IDynamicTreeProxy shapeA, IDynamicTreeProxy shapeB)
    {
        if (shapeA != shape && shapeB != shape)
            return true;
        var collider = shapeA == shape ? shapeB : shapeA;
        if (collider is not RigidBodyShape rbs || rbs.RigidBody.Data.MotionType == MotionType.Static || !rbs.RigidBody.Data.IsActive)
            return false;

        // A waterfall's terrain can sit above the falling hull. Its controller restores
        // terrain contacts on landing or recovery.
        if (rbs.RigidBody.Tag is Slave { WaterfallTransitActive: true })
            return false;

        const int spacing = Heightmap.Spacing;
        var bounds = collider.WorldBoundingBox;
        var minX = Math.Max(0, (int)MathF.Floor(bounds.Min.X / spacing));
        var minZ = Math.Max(0, (int)MathF.Floor(bounds.Min.Z / spacing));
        var maxX = Math.Min(_heightmap.QuadsX - 1, (int)MathF.Floor(bounds.Max.X / spacing));
        var maxZ = Math.Min(_heightmap.QuadsZ - 1, (int)MathF.Floor(bounds.Max.Z / spacing));
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var id = _minIndex + (ulong)_heightmap.FirstTriangleIndex(x, z);
            Register(rbs, x, z, false, id);
            Register(rbs, x, z, true, id + 1);
        }
        return false;
    }

    private void Register(RigidBodyShape shape, int x, int z, bool upper, ulong id)
    {
        if (!_heightmap.TryGetTriangle(x, z, upper, out var triangle))
            return;
        var bounds = shape.WorldBoundingBox;
        if (Math.Min(triangle.A.Y, Math.Min(triangle.B.Y, triangle.C.Y)) > bounds.Max.Y ||
            Math.Max(triangle.A.Y, Math.Max(triangle.B.Y, triangle.C.Y)) < bounds.Min.Y)
            return;

        ref var body = ref shape.RigidBody.Data;
        if (NarrowPhase.MprEpa(triangle, shape, body.Orientation, body.Position, out var pointA, out var pointB, out _, out _))
        {
            var normal = JVector.Normalize((triangle.B - triangle.A) % (triangle.C - triangle.A));
            world.RegisterContact(shape.ShapeId, id, world.NullBody, shape.RigidBody, pointA, pointB, normal);
        }
    }
}
