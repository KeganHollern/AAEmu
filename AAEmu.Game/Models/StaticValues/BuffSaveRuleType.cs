namespace AAEmu.Game.Models.StaticValues;

// This is probably just a byte, but we're reading it as a uint so ...

public enum BuffSaveRuleType : uint
{
    /// <summary>
    /// Do not persist this effect.
    /// </summary>
    DontSave = 0,
    /// <summary>
    /// Standard buffs
    /// </summary>
    Normal = 1,
    /// <summary>
    /// Premium/Crafting (Cash-Shop, Mastery 24h, Proficiency 30min)
    /// </summary>
    CharacterPersistent = 2,
    /// <summary>
    /// Special (Inn/Sleep, Battlefield, Cosmetics, Mount skins)
    /// </summary>
    Special = 3,
}
