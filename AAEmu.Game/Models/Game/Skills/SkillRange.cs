using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Physics;

namespace AAEmu.Game.Models.Game.Skills;

internal static class SkillRange
{
    internal static SkillResult Check(Skill skill, Unit caster, BaseUnit target)
    {
        var template = skill.Template;
        var minimum = (double)template.MinRange;
        var maximum = caster.ApplySkillModifiers(skill, SkillAttribute.Range, template.MaxRange);

        // Keep the existing Remove Stone (16462) compact correction.
        if (template.TargetType == SkillTargetType.Doodad && minimum >= 100)
            minimum /= 100;

        if (template.WeaponSlotForRangeId > 0)
        {
            minimum = 0;
            maximum = 3;
            if (caster.Equipment.GetItemBySlot(template.WeaponSlotForRangeId)?.Template is WeaponTemplate weapon)
            {
                minimum = weapon.HoldableTemplate.MinRange;
                maximum = weapon.HoldableTemplate.MaxRange;
            }
        }

        var distance = GetDistance(caster, target);
        if (!float.IsFinite(distance) || distance > maximum)
            return SkillResult.TooFarRange;
        return distance < minimum ? SkillResult.TooCloseRange : SkillResult.Success;
    }

    internal static float GetDistance(Unit caster, BaseUnit target)
    {
        if (target is Doodad { ParentObj: Slave parent, AttachPoint: not AttachPointKind.None } attached)
        {
            var points = SlaveGameData.Instance.GetAttachPointsForSlave(parent.ModelId);
            if (points != null && points.TryGetValue(attached.AttachPoint, out var point))
            {
                var world = parent.Transform.World;
                var position = world.Position + Vector3.Transform(point.AsPositionVector() * parent.Scale, world.ToQuaternion());
                return Math.Max(0, Vector3.Distance(caster.Transform.World.Position, position) - caster.ModelSize);
            }

            // Some legacy models have no server attachment entry. Bound those
            // interactions by the owning hull, never by an unlimited range or
            // the uninitialized repair doodad at the world origin.
            if (TryGetShipDistance(caster, parent, out var parentDistance))
                return parentDistance;
        }

        if (target is Slave slave && TryGetShipDistance(caster, slave, out var distance))
            return distance;

        return caster.GetDistanceTo(target, true);
    }

    private static bool TryGetShipDistance(Unit caster, Slave target, out float distance)
    {
        var model = ModelManager.Instance.GetShipModel(target.ModelId);
        distance = 0;
        if (model == null)
            return false;

        distance = DistanceToShip(caster.Transform.World.Position, caster.ModelSize, target, model);
        return true;
    }

    internal static float DistanceToShip(Vector3 source, float sourceRadius, Slave ship, ShipModelV1 model)
    {
        var world = ship.Transform.World;
        var scale = ship.Scale;
        if (!float.IsFinite(scale) || scale <= 0)
            return float.PositiveInfinity;

        // Use the authored mass box in game X/Y/Z coordinates. This is the
        // server's hull model, not a sphere around the ship pivot. Transform the
        // source into the box frame so rotation, tilt, scale, and offsets matter.
        var center = new Vector3(model.MassCenterX, model.MassCenterY, ShipController.ShipMassBoxDefaults.GetCenterZ(model.MassCenterZ, model.MassBoxSizeZ));
        var halfSize = new Vector3(model.MassBoxSizeX, model.MassBoxSizeY, ShipController.ShipMassBoxDefaults.GetSizeZ(model.MassBoxSizeZ)) * 0.5f;
        if (!IsFinite(center) || !IsFinite(halfSize) || halfSize.X <= 0 || halfSize.Y <= 0 || halfSize.Z < 0)
            return float.PositiveInfinity;
        var local = Vector3.Transform(source - world.Position, Quaternion.Inverse(world.ToQuaternion())) / scale;
        var nearest = Vector3.Clamp(local, center - halfSize, center + halfSize);
        return Math.Max(0, Vector3.Distance(local, nearest) * scale - sourceRadius);
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
