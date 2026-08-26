using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

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
        var shopId = stream.ReadUInt32();
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
            $"NPCObjId:{npcObjId} DoodadObjId:{doodadObjId} ShopId:{shopId} " +
            $"BuyCount:{buyCount} BuyBackCount:{buyBackCount}");

        if (useAaPoint || buyCount > StorePurchaseValidator.MaxPurchaseLines ||
            buyBackCount > StorePurchaseValidator.MaxPurchaseLines)
        {
            character.SendErrorMessage(ErrorMessageType.StoreInvalidItem);
            return;
        }

        var remotePurchase = false;
        MerchantGoods pack;

        if (npcObjId != 0)
        {
            if (doodadObjId != 0 || npc == null || !npc.Template.Merchant ||
                npc.Template.MerchantPackId == 0 || !IsNear(character, npc))
                return;
            pack = NpcManager.Instance.GetGoods(npc.Template.MerchantPackId);
        }
        else if (doodadObjId != 0)
        {
            if (doodad == null || RemoteShopCatalog.IsRemotePack(shopId) ||
                !IsNear(character, doodad))
                return;
            pack = NpcManager.Instance.GetGoods(shopId);
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

        lock (character.StorePurchaseSyncRoot)
        {
            StorePurchaseExecutor.Execute(
                character,
                pack,
                plan,
                buyBackIndices,
                remotePurchase,
                npc != null);
        }
    }

    private static bool IsNear(Character character, BaseUnit target)
    {
        var distance = MathUtil.CalculateDistance(
            character.Transform.World.Position,
            target.Transform.World.Position);
        if (distance <= 3f)
            return true;
        character.SendErrorMessage(ErrorMessageType.TooFarAway);
        return false;
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
