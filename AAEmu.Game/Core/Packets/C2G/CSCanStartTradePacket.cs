using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSCanStartTradePacket() : GamePacket(CSOffsets.CSCanStartTradePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var objId = stream.ReadBc();
        if (Connection?.ActiveChar is not { } owner)
            return;

        var target = WorldManager.Instance.GetCharacterByObjId(objId);
        if (target == null) return;
        TradeManager.Instance.CanStartTrade(owner, target);
    }
}
