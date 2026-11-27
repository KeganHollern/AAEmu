using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncPurchase : DoodadFuncTemplate
{
    // Each of these stock actions opens the gold purchase dialog for its active display.
    internal static bool OpensGoldDisplay(uint skillId) => skillId is
        14569 or // Buy
        17709 or 20218 or 20219 or // Lilyut foals
        17711 or 20215 or // Elk calves (Violet and White share the same skill)
        17537 or 20206 or 20207 or // Snowlion cubs
        17710 or 20208 or 20209 or // Leomorph cubs
        17929 or 20216 or 20217 or // Wolfhound pups
        19362 or 20220 or 20221; // Sabrefang cubs

    internal const float DisplayReach = 4f;
    internal bool UsesItemPayment => CoinItemId != 0 && CoinCount > 0;

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
        if (UsesItemPayment)
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
