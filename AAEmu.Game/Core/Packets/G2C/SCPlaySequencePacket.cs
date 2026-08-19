using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Tells the client to play a cinema/sequence (client pak cinemas/*.xml, compact `cinemas` id).
/// Opcode is present in the 1.2/r208022 offset table; the payload layout is not confirmed against
/// a retail capture — a single u32 cinema id is the minimal plausible form. Used by CinemalEffect
/// (e.g. Sharpwind Mines final-boss intro id_262_01, cinema 51). Verify against a live client
/// before relying on it for anything load-bearing. (aaemu-cluster#92)
/// </summary>
public class SCPlaySequencePacket(uint cinemaId) : GamePacket(SCOffsets.SCPlaySequencePacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(cinemaId);
        return stream;
    }
}
