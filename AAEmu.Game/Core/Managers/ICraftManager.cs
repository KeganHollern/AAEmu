using AAEmu.Game.Models.Game.Crafts;

namespace AAEmu.Game.Core.Managers;

public interface ICraftManager : ILoadable
{
    Craft GetCraftById(uint craftId);
    bool TryGetCraftById(uint craftId, out Craft craft);
    bool IsCraftInPack(uint craftId, uint craftPackId);
}
