using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncPurchase : DoodadFuncTemplate
{
    public uint ItemId { get; set; }
    public int Count { get; set; }
    public uint CoinItemId { get; set; }
    public int CoinCount { get; set; }
    public uint CurrencyId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (caster is not Character character || !ServiceInteraction.CanReach(character, owner))
            return;
        lock (SaveManager.PersistenceSyncRoot)
        {
            var error = TryPurchase(character);
            if (error != ErrorMessageType.NoErrorMessage)
                character.SendErrorMessage(error);
        }
    }

    internal ErrorMessageType TryPurchase(Character character)
    {
        var template = ItemManager.Instance.GetTemplate(ItemId);
        if (template == null || Count <= 0 || CurrencyId != 0)
            return ErrorMessageType.StoreInvalidItem;
        var backpack = ItemManager.Instance.IsAutoEquipTradePack(ItemId);
        var destination = backpack ? character.Inventory.Equipment : character.Inventory.Bag;
        if (backpack && character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) != null)
            return ErrorMessageType.BackpackOccupied;
        using var mutation = new InventoryMutation(ItemTaskType.DoodadInteraction);
        // r208022 selects item payment only when both authored values are positive.
        if (CoinItemId != 0 && CoinCount > 0)
        {
            if (!InventoryPayment.TryConsume(mutation, character.Inventory.Bag, CoinItemId, CoinCount))
                return ErrorMessageType.NotEnoughItem;
        }
        else
        {
            var cost = (long)template.Price * Count;
            // The zero-price test design and missing templates have no supported purchase.
            if (cost <= 0 || cost > int.MaxValue)
                return ErrorMessageType.StoreInvalidItem;
            if (!mutation.TryChangeMoney(character, -(int)cost))
                return ErrorMessageType.NotEnoughMoney;
        }
        if (!mutation.TryGrant(destination, ItemId, Count))
            return backpack ? ErrorMessageType.BackpackOccupied : ErrorMessageType.BagFull;
        mutation.Complete();
        return ErrorMessageType.NoErrorMessage;
    }
}
