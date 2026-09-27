using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSJuryEndTestimonyPacket() : GamePacket(CSOffsets.CSJuryEndTestimonyPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (stream.LeftBytes != 8)
            throw new InvalidDataException("CSJuryEndTestimonyPacket requires exactly 8 bytes.");

        var trialId = stream.ReadUInt32();
        var juryId = stream.ReadInt32();

        Logger.Info($"JuryEndTestimony, {Connection.ActiveChar.Name}, Trial: {trialId}, Jury: {juryId}");
        TrialManager.Instance.JuryEndTestimony(Connection.ActiveChar, trialId, juryId);
    }
}
