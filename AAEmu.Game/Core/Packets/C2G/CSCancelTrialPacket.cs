using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSCancelTrialPacket() : GamePacket(CSOffsets.CSCancelTrialPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (stream.LeftBytes != 4)
            throw new InvalidDataException("CSCancelTrialPacket requires exactly 4 bytes.");

        var trial = stream.ReadUInt32();
        Logger.Warn($"CancelTrial, Trial: {trial}");
        TrialManager.Instance.CancelTrial(Connection.ActiveChar, trial);
    }
}
