using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.Game.Models.Game.Skills;

internal static class SkillCastAuthorization
{
    internal static bool CanUseCharacterSkill(Character character, SkillTemplate skill,
        SkillCaster source, SkillCastTarget target)
    {
        if (character == null || skill == null || skill.Id == 0 ||
            source is not SkillCasterUnit || source.ObjId != character.ObjId)
            return false;

        // AutoLearn and NeedLearn describe template behavior, not a grant to this player.
        if (skill.Id is 2 or 3 or 4 || SkillManager.Instance.IsDefaultSkill(skill.Id) ||
            character.Skills.Skills.ContainsKey(skill.Id) ||
            SkillGrantGameData.Instance.HasBuffGrant(skill.Id, character.Buffs) ||
            PriestSkillAuthorization.CanUse(character, skill, source, target) ||
            MateRecovery.CanUseGetUp(character, skill, source, target))
            return true;

        var world = character.ParentWorld;
        if (world == null || target == null)
            return false;

        if (skill.TargetType == SkillTargetType.Building &&
            target.Type is SkillCastTargetType.Unit or SkillCastTargetType.Doodad &&
            world.GetBaseUnit(target.ObjId) is { } building &&
            building is House or Shipyard.Shipyard)
        {
            return ReferenceEquals(building.ParentWorld, world) &&
                building.Transform.InstanceId == character.Transform.InstanceId &&
                (building switch
                {
                    House house => house.CurrentStep >= 0 &&
                        house.Template.BuildSteps.TryGetValue(house.CurrentStep, out var step) &&
                        step.SkillId == skill.Id,
                    Shipyard.Shipyard shipyard => shipyard.CanUseConstructionSkill(skill.Id),
                    _ => false
                }) && building.AllowedToInteract(character);
        }

        if (target is SkillCastDoodadTarget)
        {
            var doodad = world.GetDoodad(target.ObjId);
            return doodad != null && ReferenceEquals(doodad.ParentWorld, world) &&
                doodad.Transform.InstanceId == character.Transform.InstanceId &&
                SkillGrantGameData.Instance.HasDoodadGrant(doodad.FuncGroupId, skill.Id);
        }

        if (target is SkillCastUnitTarget)
        {
            var npc = world.GetNpc(target.ObjId);
            return npc?.Template != null && !npc.Despawned && ReferenceEquals(npc.ParentWorld, world) &&
                npc.Transform.InstanceId == character.Transform.InstanceId &&
                SkillGrantGameData.Instance.HasNpcGrant((uint)npc.Template.NpcInteractionSetId, skill.Id);
        }

        return false;
    }
}
