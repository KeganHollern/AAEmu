using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSTradeLockPacket() : GamePacket(CSOffsets.CSTradeLockPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var _lock = stream.ReadBoolean();
        if (Connection?.ActiveChar is { } character)
            TradeManager.Instance.LockTrade(character, _lock);
    }
}
