using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Quests;

internal static class QuestInteraction
{
    // r208022 393761f0 / 3937aad0 -> 39093860: sqrt(9), strict shape-edge distance.
    private const double NpcInteractionRange = 3d;
    // The doodad overload (39093800) uses the supplied 9 directly, without a square root.
    private const double DoodadInteractionRange = 9d;

    internal static bool IsVisibleSource(Character owner, BaseUnit source)
    {
        if (owner?.ParentWorld == null || source?.ParentWorld != owner.ParentWorld ||
            owner.Transform == null || source.Transform == null ||
            owner.Transform.WorldId != source.Transform.WorldId ||
            owner.Transform.InstanceId != source.Transform.InstanceId ||
            owner.Region == null || source.Region == null ||
            !owner.CanSeeTarget(source))
            return false;

        // Compare region objects, not their coordinates, which can repeat in other instances.
        return owner.Region.GetNeighbors()?.Any(region => ReferenceEquals(region, source.Region)) == true;
    }

    internal static bool CanInteractWithNpc(Character owner, Npc npc)
    {
        if (!IsVisibleSource(owner, npc) || owner.ModelId == 0 || npc.ModelId == 0)
            return false;

        return IsWithinNpcRange(owner, ModelManager.Instance.GetActorModel(owner.ModelId),
            npc, ModelManager.Instance.GetActorModel(npc.ModelId));
    }

    internal static bool IsWithinNpcRange(Unit owner, ActorModel ownerModel, Unit npc, ActorModel npcModel)
    {
        // Unknown/non-actor collision models must not silently become points or guessed radii.
        if (!TryGetSphere(owner, ownerModel, out var ownerCenter, out var ownerRadius) ||
            !TryGetSphere(npc, npcModel, out var npcCenter, out var npcRadius))
            return false;

        return SphereEdgeDistance(ownerCenter, ownerRadius, npcCenter, npcRadius) < NpcInteractionRange;
    }

    internal static bool CanInteractWithDoodad(Character owner, Doodad doodad)
    {
        if (!IsVisibleSource(owner, doodad) || owner.ModelId == 0)
            return false;

        return IsWithinDoodadRange(owner, ModelManager.Instance.GetActorModel(owner.ModelId),
            doodad, QuestDoodadInteractionShapes.Get(doodad));
    }

    internal static bool IsWithinDoodadRange(Unit owner, ActorModel ownerModel, Doodad doodad,
        IReadOnlyList<QuestInteractionSphere> spheres)
    {
        if (!TryGetSphere(owner, ownerModel, out var ownerCenter, out var ownerRadius) || spheres == null)
            return false;

        foreach (var sphere in spheres)
        {
            if (TryTransformSphere(doodad, sphere.Center, sphere.Radius, out var center, out var radius) &&
                SphereEdgeDistance(ownerCenter, ownerRadius, center, radius) < DoodadInteractionRange)
                return true;
        }
        return false;
    }

    private static double SphereEdgeDistance(Vector3 source, float sourceRadius, Vector3 target, float targetRadius)
    {
        var dx = (double)source.X - target.X;
        var dy = (double)source.Y - target.Y;
        var dz = (double)source.Z - target.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) - sourceRadius - targetRadius;
    }

    private static bool TryGetSphere(Unit unit, ActorModel model, out Vector3 center, out float radius)
    {
        center = default;
        radius = 0;
        if (model == null || !float.IsFinite(model.Height))
            return false;

        // ActorUnitModel::Init (390d5880) uses (0, 0, height) and radius from actor_models.
        // The sphere transform (3989b100) applies model scale, rotation, then world translation.
        return TryTransformSphere(unit, new Vector3(0, 0, model.Height), model.Radius, out center, out radius);
    }

    private static bool TryTransformSphere(BaseUnit unit, Vector3 localCenter, float localRadius,
        out Vector3 center, out float radius)
    {
        center = default;
        radius = 0;
        if (unit?.Transform == null || !float.IsFinite(unit.Scale) || unit.Scale <= 0 ||
            !float.IsFinite(localRadius) || localRadius < 0)
            return false;

        var transform = unit.Transform.World;
        center = transform.Position + Vector3.Transform(localCenter * unit.Scale, transform.ToQuaternion());
        radius = localRadius * unit.Scale;
        return float.IsFinite(center.X) && float.IsFinite(center.Y) && float.IsFinite(center.Z) && float.IsFinite(radius);
    }
}
