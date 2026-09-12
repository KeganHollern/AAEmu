using System.Numerics;

namespace AAEmu.Game.Models.Game.DoodadObj;

internal static class PlayerDoodadPlacementRules
{
    // r208022 FUN_393a8fa0 compares the full squared player distance with 900.
    internal const float MaximumDistance = 30f;

    internal static bool Check(Vector3 playerPosition, Vector3 position, float rotation, float scale,
        uint restrictedZoneGroupId, uint zoneGroupId)
    {
        return IsFinite(playerPosition) && IsFinite(position) && float.IsFinite(rotation) &&
            float.IsFinite(scale) && scale > 0 && zoneGroupId != 0 &&
            Vector3.DistanceSquared(playerPosition, position) <= MaximumDistance * MaximumDistance &&
            (restrictedZoneGroupId == 0 || restrictedZoneGroupId == zoneGroupId);
    }

    private static bool IsFinite(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
}
