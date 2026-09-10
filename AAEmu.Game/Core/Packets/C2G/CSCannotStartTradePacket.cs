using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSCannotStartTradePacket() : GamePacket(CSOffsets.CSCannotStartTradePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var objId = stream.ReadBc();
        var reason = stream.ReadInt32();

        if (Connection?.ActiveChar is { } character)
            TradeManager.Instance.DeclineTrade(character, objId, reason);
    }
}
