using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills;

internal static class MountSeatAuthorization
{
    // r208022 sends CSMountMate below 3m and CSBindSlave below 4m.
    internal const float MateRange = 3f;
    internal const float SlaveRange = 4f;

    internal static bool SameLivingWorld(Character actor, Unit mount) =>
        actor != null && mount != null && !actor.IsDead && !mount.IsDead &&
        actor.ParentWorld != null && ReferenceEquals(actor.ParentWorld, mount.ParentWorld) &&
        actor.Transform.InstanceId == mount.Transform.InstanceId &&
        actor.Transform.WorldId == mount.Transform.WorldId;

    internal static bool CanEnter(Character actor, Unit mount, Vector3 seatPosition, float range) =>
        SameLivingWorld(actor, mount) && !mount.AttachmentsRetired && actor.AttachedPoint == AttachPointKind.None &&
        !actor.IsRiding && actor.Transform.Parent == null && actor.Bonding == null &&
        InRange(actor.Transform.World.Position, seatPosition, range);

    internal static bool InRange(Vector3 position, Vector3 seatPosition, float range)
    {
        var distanceSquared = Vector3.DistanceSquared(position, seatPosition);
        return float.IsFinite(distanceSquared) && distanceSquared < range * range;
    }

    internal static bool IsAttached(Character actor, Unit mount) =>
        actor.AttachedPoint != AttachPointKind.None &&
        ReferenceEquals(actor.Transform.Parent, mount.Transform);
}
