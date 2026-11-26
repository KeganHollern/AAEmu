using System.Numerics;

using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.Game.Models.Game.World;

public partial class Region
{
    internal readonly record struct DoodadOrigin(Doodad Doodad, Vector3 Position);
    private DoodadOrigin[] _doodadOrigins = [];

    internal static bool IsStaticDoodadCandidate(Doodad doodad) =>
        doodad is { Template: { ModelKindId: 1, LoadModelFromWorld: false, NoCollision: false,
            GrowthTime: 0, TotalDoodadGrowthTime: 0 }, Spawner: { FollowNpc: 0, FollowPath: "", Position: not null },
            OwnerType: DoodadOwnerType.System, OwnerId: 0, ParentObj: null, ParentObjId: 0 } &&
        ReferenceEquals(doodad.Spawner.ParentWorld, doodad.ParentWorld) &&
        doodad.Transform.Parent == null && doodad.Transform.StickyParent == null;

    // Region membership writes already hold _objectsLock. Readers need only the nearby origins.
    private void AddDoodadOrigin(Doodad doodad)
    {
        var origin = doodad.Spawner.Position.AsPositionVector();
        if (doodad.Transform.World.Position != origin)
            return;
        var old = _doodadOrigins.AsSpan();
        var index = FirstOriginAt(old, origin.X);
        var next = new DoodadOrigin[old.Length + 1];
        old[..index].CopyTo(next);
        next[index] = new DoodadOrigin(doodad, origin);
        old[index..].CopyTo(next.AsSpan(index + 1));
        Volatile.Write(ref _doodadOrigins, next);
        _worldInstance.DoodadFloors.Update(doodad, this);
    }

    internal void RefreshDoodadFloor(Doodad doodad)
    {
        if (doodad.Spawner?.Position == null)
            return;
        // Region can still point here during despawn. Only a registered origin can update the grid.
        lock (_objectsLock)
        {
            var origin = doodad.Spawner.Position.X;
            foreach (var entry in DoodadOriginsFrom(origin))
            {
                if (entry.Position.X > origin)
                    break;
                if (ReferenceEquals(entry.Doodad, doodad))
                {
                    _worldInstance.DoodadFloors.Update(doodad, this);
                    break;
                }
            }
        }
    }

    private void RemoveDoodadOrigin(Doodad doodad)
    {
        var old = _doodadOrigins.AsSpan();
        for (var i = 0; i < old.Length; i++)
        {
            if (old[i].Doodad != doodad)
                continue;
            var next = new DoodadOrigin[old.Length - 1];
            old[..i].CopyTo(next);
            old[(i + 1)..].CopyTo(next.AsSpan(i));
            Volatile.Write(ref _doodadOrigins, next);
            _worldInstance.DoodadFloors.Remove(doodad, this);
            return;
        }
    }

    internal ReadOnlySpan<DoodadOrigin> DoodadOriginsFrom(float x)
    {
        var origins = Volatile.Read(ref _doodadOrigins);
        return origins.AsSpan(FirstOriginAt(origins, x));
    }

    private static int FirstOriginAt(ReadOnlySpan<DoodadOrigin> origins, float x)
    {
        var low = 0;
        var high = origins.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (origins[mid].Position.X < x)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }
}
