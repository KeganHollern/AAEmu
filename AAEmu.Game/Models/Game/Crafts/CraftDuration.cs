using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.Game.Models.Game.Crafts;

internal static class CraftDuration
{
    internal static int GetBaseMilliseconds(Craft craft, SkillTemplate skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return craft?.CastDelay ?? skill.CastingTime;
    }
}
