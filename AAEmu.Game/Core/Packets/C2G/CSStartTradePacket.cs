using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSStartTradePacket() : GamePacket(CSOffsets.CSStartTradePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var objId = stream.ReadBc();
        if (Connection?.ActiveChar is not { } target)
            return;

        var owner = WorldManager.Instance.GetCharacterByObjId(objId);
        if (owner == null) return;
        TradeManager.Instance.StartTrade(owner, target);
    }
}
