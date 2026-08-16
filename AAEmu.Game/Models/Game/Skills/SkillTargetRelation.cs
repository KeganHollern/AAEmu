namespace AAEmu.Game.Models.Game.Skills;

public enum SkillTargetRelation : byte
{
    Any = 0,
    Friendly = 1,
    Party = 2,
    Raid = 3,
    Hostile = 4,
    Others = 5,

    /// <summary>
    /// Area-tick relation used by spreading debuffs (e.g. Electric Shock,
    /// buff 250): hits the current buff carrier and units friendly to the
    /// carrier ("you and allies within Xm"), never the original caster or
    /// other hostiles. Evaluated relative to the carrier in
    /// BuffTemplate.DoAreaTick, not to the caster.
    /// </summary>
    CarrierAndFriendly = 6
}
