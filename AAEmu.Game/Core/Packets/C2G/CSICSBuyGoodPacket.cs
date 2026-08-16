using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.CashShop;
using AAEmu.Game.Models.Tasks.CashShop;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSICSBuyGoodPacket() : GamePacket(CSOffsets.CSICSBuyGoodPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var buyer = Connection.ActiveChar;
        var buyList = new List<IcsSku>();
        var thisChar = Connection.ActiveChar;
        byte buyMode = 1; // No idea what this means

        var numBuys = stream.ReadByte();
        for (var i = 0; i < numBuys; i++)
        {
            var cashShopId = stream.ReadUInt32();
            var mainTab = stream.ReadByte();
            var subTab = stream.ReadByte();
            var detailIndex = stream.ReadByte();

            if (!CashShopManager.Instance.ShopItems.TryGetValue(cashShopId, out var shopItem))
            {
                Logger.Warn($"{Connection.ActiveChar.Name} is trying to shop for invalid ShopItem: {cashShopId}");
                continue;
            }

            var idx = 0;
            IcsSku sku = null;
            foreach (var (key, detail) in shopItem.Skus)
            {
                if (idx == detailIndex)
                {
                    sku = detail;
                    break;
                }
                idx++;
            }

            if (sku == null)
            {
                Logger.Warn(
                    $"{Connection.ActiveChar.Name} is trying to shop from ShopItem: {shopItem.ShopId}, but with invalid index: {detailIndex}");
                continue;
            }

            buyList.Add(sku);
        }

        var receiverName = stream.ReadString();

        // Default target: the buyer themselves
        var targetId = thisChar.Id;
        var targetAccountId = thisChar.AccountId;
        var targetName = thisChar.Name;

        if (receiverName != string.Empty)
        {
            // Gifts are delivered by mail, so the receiver does not need to be
            // online: resolve the identity from the name cache instead of the
            // online-character table.
            targetId = NameManager.Instance.GetCharacterId(receiverName.NormalizeName());
            if (targetId == 0)
            {
                thisChar.SendErrorMessage(ErrorMessageType.IngameShopFindCharacterNameFail);
                thisChar.SendPacket(new SCICSBuyResultPacket(false, buyMode, receiverName, 0));
                return;
            }

            targetName = NameManager.Instance.GetCharacterName(targetId) ?? receiverName;
            targetAccountId = NameManager.Instance.GetCharacterAccount(targetId);
        }

        if (buyList.Count <= 0)
        {
            thisChar.SendErrorMessage(ErrorMessageType.BuyCartEmpty);
            Connection.ActiveChar.SendPacket(new SCICSBuyResultPacket(false, buyMode, receiverName, 0));
            return;
        }

        // Create task for the transaction, this allows handling of credits in a async manner
        TaskManager.Instance.Schedule(new CashShopBuyTask(buyMode, Connection.ActiveChar, targetId, targetAccountId, targetName, buyList), TimeSpan.FromSeconds(1));
    }
}
