using AAEmu.Game.Core.Managers;

namespace AAEmu.Game.Models.Tasks.World;

public sealed class CurrencyShopMerchantDespawnTask(uint characterObjId, uint npcObjId) : Task
{
    public override void Execute()
    {
        CurrencyShopManager.Close(characterObjId, npcObjId);
    }
}
