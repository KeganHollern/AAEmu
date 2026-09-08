using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.Game.Core.Managers;

public interface ITradeManager
{
    void CanStartTrade(Character owner, Character target);
    void StartTrade(Character owner, Character target);
    void DeclineTrade(Character character, uint ownerObjId, int reason);
    void CancelTrade(Character character, int reason);
    void AddItem(Character character, SlotType slotType, byte slot, int amount);
    void RemoveItem(Character character, SlotType slotType, byte slot);
    void AddMoney(Character character, int moneyAmount);
    void LockTrade(Character character, bool locked);
    void OkTrade(Character character);
}
