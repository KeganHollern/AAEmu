using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSBuyItemsPacket() : GamePacket(CSOffsets.CSBuyItemsPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var character = Connection.ActiveChar;
        var npcObjId = stream.ReadBc();
        var npc = npcObjId == 0 ? null : character.ParentWorld.GetNpc(npcObjId);
        var doodadObjId = stream.ReadBc();
        var doodad = doodadObjId == 0 ? null : character.ParentWorld.GetDoodad(doodadObjId);
        var storeType = stream.ReadUInt32(); // Normal shops write zero. A gold display writes its active doodad function key.
        var buyCount = stream.ReadByte();
        var buyBackCount = stream.ReadByte();

        var requests = new List<StorePurchaseRequest>(buyCount);
        for (var i = 0; i < buyCount; i++)
        {
            var itemId = stream.ReadUInt32();
            _ = stream.ReadByte(); // The merchant pack, not the client, owns the item grade.
            var count = stream.ReadInt32();
            var currency = (ShopCurrencyType)stream.ReadByte();
            requests.Add(new StorePurchaseRequest(itemId, count, currency));
        }

        var buyBackIndices = new List<int>(buyBackCount);
        for (var i = 0; i < buyBackCount; i++)
            buyBackIndices.Add(stream.ReadInt32());

        var useAaPoint = stream.ReadBoolean();
        Logger.Debug(
            $"NPCObjId:{npcObjId} DoodadObjId:{doodadObjId} StoreType:{storeType} " +
            $"BuyCount:{buyCount} BuyBackCount:{buyBackCount}");

        if (useAaPoint || buyCount > StorePurchaseValidator.MaxPurchaseLines ||
            buyBackCount > StorePurchaseValidator.MaxPurchaseLines)
        {
            character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
            return;
        }

        lock (character.StorePurchaseSyncRoot)
        {
            var remotePurchase = false;
            MerchantGoods pack;

            if (npcObjId != 0)
            {
                if (doodadObjId != 0 || npc == null || !npc.Template.Merchant ||
                    npc.Template.MerchantPackIds.Count == 0)
                    return;
                if (!ServiceInteraction.CanReachMerchant(character, npc))
                {
                    character.SendErrorMessage(ErrorMessageType.TooFarAway);
                    return;
                }
                pack = NpcManager.Instance.GetNpcGoods(npc.Template);
            }
            else if (doodadObjId != 0)
            {
                // StoreUi opens locally in r208022. No server interaction session precedes this request.
                var display = doodad?.CurrentFuncs.FirstOrDefault(func => func.FuncKey == storeType &&
                    DoodadFuncPurchase.OpensGoldDisplay(func.SkillId) && func.FuncType == nameof(DoodadFuncPurchase));
                if (doodad == null || doodad.Despawn > DateTime.MinValue ||
                    !ServiceInteraction.CanReach(character, doodad, display == null ? 3f : DoodadFuncPurchase.DisplayReach))
                {
                    character.SendErrorMessage(ErrorMessageType.TooFarAway);
                    return;
                }
                if (display != null)
                {
                    ConfirmDisplayPurchase(character, doodad, display, requests, buyBackCount);
                    return;
                }

                var store = doodad.CurrentFuncs.FirstOrDefault(func => func.FuncType == nameof(DoodadFuncStoreUi));
                if (store == null || DoodadManager.Instance.GetFuncTemplate(store.FuncId, store.FuncType)
                    is not DoodadFuncStoreUi storeTemplate)
                {
                    character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
                    return;
                }
                if (!DoodadPermissionRules.Allows(character, doodad, store.PermId))
                {
                    character.SendErrorMessage(ErrorMessageType.InteractionPermissionDeny);
                    return;
                }
                // Honor/Vocation packs can also belong to an authored doodad shop.
                pack = NpcManager.Instance.GetGoods(storeTemplate.MerchantPackId);
            }
            else
            {
                if (!RemoteShopCatalog.TryGetPackId(requests, out var merchantPackId))
                {
                    character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
                    return;
                }

                remotePurchase = true;
                pack = NpcManager.Instance.GetGoods(merchantPackId);
            }

            if (pack == null)
            {
                character.SendErrorMessage(ErrorMessageType.StoreHaveProblem);
                return;
            }

            StorePurchasePlan plan = null;
            if (requests.Count > 0 &&
                !StorePurchaseValidator.TryCreatePlan(
                    pack,
                    requests,
                    ItemManager.Instance.GetTemplate,
                    out plan,
                    out var validationError))
            {
                SendPurchaseError(validationError);
                return;
            }

            StorePurchaseExecutor.Execute(
                character,
                pack,
                plan,
                buyBackIndices,
                remotePurchase,
                npc != null);
        }
    }

    private static void ConfirmDisplayPurchase(Character character, Doodad doodad, DoodadFunc display,
        List<StorePurchaseRequest> requests, int buyBackCount)
    {
        if (!DoodadPermissionRules.Allows(character, doodad, display.PermId))
        {
            character.SendErrorMessage(ErrorMessageType.InteractionPermissionDeny);
            return;
        }

        if (DoodadManager.Instance.GetFuncTemplate(display.FuncId, display.FuncType) is not DoodadFuncPurchase purchase ||
            purchase.UsesItemPayment || purchase.CurrencyId != 0 || buyBackCount != 0 ||
            requests is not [{ Currency: ShopCurrencyType.Money } line] ||
            line.ItemId != purchase.ItemId || line.Count != purchase.Count)
        {
            character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
            return;
        }

        if (display.SkillId == DoodadFuncPurchase.BuyDragoraTicket)
        {
            // The booth checks the quest and enters its timed phase only after payment.
            doodad.Use(character, display.SkillId);
            return;
        }

        purchase.CompleteDisplayPurchase(character, doodad, display);
    }

    private void SendPurchaseError(StorePurchaseError error)
    {
        var message = error switch
        {
            StorePurchaseError.NotEnoughMoney => ErrorMessageType.NotEnoughMoney,
            StorePurchaseError.NotEnoughHonor => ErrorMessageType.NotEnoughHonorPoint,
            StorePurchaseError.NotEnoughVocationBadges => ErrorMessageType.NotEnoughLivingPoint,
            _ => ErrorMessageType.StoreInvalidItem
        };
        Connection.ActiveChar.SendErrorMessage(message);
    }
}
