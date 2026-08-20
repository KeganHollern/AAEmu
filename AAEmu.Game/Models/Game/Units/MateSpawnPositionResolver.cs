using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.Units;

internal static class MateSpawnPositionResolver
{
    internal const float MaximumGroundHeightDelta = 5f;

    internal static bool RequiresGrounding(ActorModel actorModel)
    {
        if (actorModel is null)
            return true;

        return !actorModel.UnderwaterCreature && actorModel.MovementId is not 2 and not 3;
    }

    internal static bool TryResolve(
        PositionAndRotation source,
        float angle,
        float distance,
        ActorModel actorModel,
        AiGeoDataManager geoData,
        out PositionAndRotation spawnPosition)
    {
        float? ResolveGroundHeight(Vector3 position)
        {
            return geoData is not null && geoData.TryGetGroundHeight(position, out var height)
                ? height
                : null;
        }

        return TryResolve(
            source,
            angle,
            distance,
            RequiresGrounding(actorModel),
            ResolveGroundHeight,
            out spawnPosition);
    }

    internal static bool TryResolve(
        PositionAndRotation source,
        float angle,
        float distance,
        bool requiresGrounding,
        Func<Vector3, float?> groundHeightResolver,
        out PositionAndRotation spawnPosition)
    {
        spawnPosition = source.Clone();
        if (!IsFinite(source.Position) || !float.IsFinite(source.Rotation.Z) ||
            !float.IsFinite(angle) || !float.IsFinite(distance))
            return false;

        spawnPosition.Rotate(0f, 0f, angle);
        spawnPosition.AddDistanceToFront(distance);

        if (!requiresGrounding)
            return IsFinite(spawnPosition.Position);

        var groundHeight = groundHeightResolver(spawnPosition.Position);
        if (!groundHeight.HasValue || !float.IsFinite(groundHeight.Value) ||
            MathF.Abs(spawnPosition.Position.Z - groundHeight.Value) > MaximumGroundHeightDelta)
            return false;

        spawnPosition.SetHeight(groundHeight.Value);
        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
