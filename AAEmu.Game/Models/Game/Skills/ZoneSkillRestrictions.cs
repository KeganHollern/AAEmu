using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Zones;

namespace AAEmu.Game.Models.Game.Skills;

public static class ZoneSkillRestrictions
{
    public static Item GetSourceItem(BaseUnit caster, SkillCaster skillCaster)
    {
        // ItemTemplateId in SkillItem is packet data. Use the actual caster's inventory.
        return skillCaster is SkillItem itemCaster && caster is Character character
            ? character.Inventory?.GetItemById(itemCaster.ItemId)
            : null;
    }

    public static ZoneGroupBannedTag GetSkillBan(BaseUnit caster, uint skillId, SkillCaster skillCaster)
    {
        return GetBan(caster, skillId, GetSourceItem(caster, skillCaster)?.TemplateId ?? 0);
    }

    public static ZoneGroupBannedTag GetItemBan(BaseUnit caster, Item item, Vector3? destination = null)
    {
        return item == null ? null : GetBan(caster, item.Template.UseSkillId, item.TemplateId, destination);
    }

    public static ZoneGroupBannedTag GetBan(BaseUnit caster, uint skillId, uint itemId = 0, Vector3? destination = null)
    {
        var world = caster?.ParentWorld;
        if (world == null)
            return null;
        var zones = ZoneManager.Instance;
        if (!zones.HasBannedTags)
            return null;

        // Transform.ZoneId can describe the previous region or a cloned source transform.
        // Resolve the authoritative world coordinates at each action, including cast completion.
        var position = caster.Transform.World.Position;
        var groupId = GetGroup(position);
        var ban = zones.GetBannedAction(groupId, skillId, itemId);
        if (ban != null || destination == null)
            return ban;
        var destinationGroupId = GetGroup(destination.Value);
        return destinationGroupId == groupId ? null : zones.GetBannedAction(destinationGroupId, skillId, itemId);

        uint GetGroup(Vector3 point)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                return 0;
            var zoneKey = WorldManager.Instance.GetZoneId(world.Template, point.X, point.Y);
            return zones.GetZoneByKey(zoneKey)?.GroupId ?? 0;
        }
    }

    public static bool CanUseItem(BaseUnit caster, Item item, Vector3? destination = null)
    {
        if (GetItemBan(caster, item, destination) == null)
            return true;
        (caster as Character)?.SendErrorMessage(ErrorMessageType.ItemCannotUseHere);
        return false;
    }

    public static bool CanApply(BaseUnit caster, Skill skill, SkillCaster skillCaster, Vector3? destination = null)
    {
        caster = skill?.OriginalCaster ?? caster;
        var sourceItemId = skill?.SourceItemTemplateId ?? 0;
        if (sourceItemId == 0)
            sourceItemId = GetSourceItem(caster, skillCaster)?.TemplateId ?? 0;
        if (GetBan(caster, skill?.Template?.Id ?? skill?.Id ?? 0, sourceItemId, destination) == null)
            return true;
        if (skill != null)
            skill.Cancelled = true;
        (caster as Character)?.SendErrorMessage(ErrorMessageType.SkillCannotUseHere);
        return false;
    }
}
