using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    /// <summary>Includes a stored sentence that still needs login recovery.</summary>
    public bool IsPrisoner => OfflineGuiltyTime > 0 || Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner);
}
