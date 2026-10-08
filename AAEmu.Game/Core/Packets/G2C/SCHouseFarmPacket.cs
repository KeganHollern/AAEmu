using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Emits HOUSE_FARM_MSG, which the r208022 client prints as "[name] harvestable/total" in chat.
/// This packet does not open a garden window. The retail send trigger is not yet confirmed.
/// </summary>
public class SCHouseFarmPacket(string name, int total, int harvestable) : GamePacket(SCOffsets.SCHouseFarmPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(name);
        stream.Write(total);
        stream.Write(harvestable);
        return stream;
    }
}
