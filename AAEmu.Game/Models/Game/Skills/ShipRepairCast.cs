using System.Numerics;

using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Skills;

internal sealed class ShipRepairCast
{
    private readonly Skill _skill;
    private readonly Doodad _point;
    private readonly Slave _ship;
    private readonly uint _phase;
    private readonly Vector3 _startOnDeck;

    private ShipRepairCast(Skill skill, Doodad point, Slave ship, Unit caster)
    {
        _skill = skill;
        _point = point;
        _ship = ship;
        _phase = point.FuncGroupId;
        var pose = ship.Transform.World;
        _startOnDeck = Vector3.Transform(caster.Transform.World.Position - pose.Position,
            Quaternion.Inverse(pose.ToQuaternion())) / ship.Scale;
    }

    internal static ShipRepairCast Start(Skill skill, BaseUnit caster, BaseUnit target)
        => skill.Id == SkillsEnum.RepairShip && caster is Unit unit &&
           target is Doodad { ParentObj: Slave ship, AttachPoint: >= AttachPointKind.HealPoint0 and <= AttachPointKind.HealPoint9 } point
            ? new ShipRepairCast(skill, point, ship, unit)
            : null;

    internal bool CanContinue(Unit caster)
    {
        var support = caster.Transform.Parent;
        if (support != null && support != _ship.Transform && !ReferenceEquals(support.GameObject?.ParentObj, _ship))
            return false;
        if (!ReferenceEquals(caster.ParentWorld, _ship.ParentWorld) ||
            caster.Transform.InstanceId != _ship.Transform.InstanceId ||
            _point.Despawn != default || _point.FuncGroupId != _phase || !_ship.AttachedDoodads.Contains(_point))
            return false;

        // Deck contact can change while the actor stays in the same footprint. Use the original
        // position for the whole cast so small steps cannot accumulate into a walk across the deck.
        var displacement = caster.Transform.World.Position - _ship.Transform.TransformPoint(_startOnDeck);
        return displacement.LengthSquared() <= caster.ModelSize * caster.ModelSize &&
               SkillRange.Check(_skill, caster, _point) == SkillResult.Success;
    }
}
