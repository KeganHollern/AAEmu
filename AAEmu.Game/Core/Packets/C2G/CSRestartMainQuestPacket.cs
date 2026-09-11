using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSRestartMainQuestPacket() : GamePacket(CSOffsets.CSRestartMainQuestPacket, 1)
{
    internal uint QuestContextId { get; private set; }

    public override void Read(PacketStream stream)
    {
        if (stream.LeftBytes != sizeof(uint))
            throw new InvalidDataException("Main quest restart requires one quest context ID.");

        QuestContextId = stream.ReadUInt32();
    }

    public override void Execute()
    {
        Connection.ActiveChar.Quests.RestartMainQuest(QuestContextId);
    }
}
