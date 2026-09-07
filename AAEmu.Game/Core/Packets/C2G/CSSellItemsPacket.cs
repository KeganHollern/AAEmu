using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Merchant;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSellItemsPacket() : GamePacket(CSOffsets.CSSellItemsPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var npcObjId = stream.ReadBc();
        _ = stream.ReadBc(); // Existing, unconfirmed secondary object field.
        var count = stream.ReadByte();
        var requests = new List<MerchantSaleRequest>(count);
        for (var i = 0; i < count; i++)
        {
            var slotType = (SlotType)stream.ReadByte();
            var slot = stream.ReadByte();
            var itemId = stream.ReadUInt64();
            _ = stream.ReadUInt32(); // Unconfirmed field; this is not an authoritative quantity.
            requests.Add(new MerchantSaleRequest(slotType, slot, itemId));
        }

        var character = Connection.ActiveChar;
        var result = MerchantSaleExecutor.Execute(character, npcObjId, requests);
        if (result != MerchantSaleResult.Success)
        {
            // Execute has disposed any failed mutation before an error can trigger callbacks.
            character?.SendErrorMessage(result switch
            {
                MerchantSaleResult.TooFarAway => ErrorMessageType.TooFarAway,
                MerchantSaleResult.InvalidItem => ErrorMessageType.StoreInvalidItem,
                _ => ErrorMessageType.StoreHaveProblem
            });
        }
    }
}
