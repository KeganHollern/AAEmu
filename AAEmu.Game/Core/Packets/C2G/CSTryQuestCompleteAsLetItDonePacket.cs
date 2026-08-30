using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSTryQuestCompleteAsLetItDonePacket() : GamePacket(CSOffsets.CSTryQuestCompleteAsLetItDonePacket, 1)
{
    private uint _id;
    private uint _objId;
    private int _selected;

    //

    public override void Read(PacketStream stream)
    {
        _id = stream.ReadUInt32();
        _objId = stream.ReadBc();
        _selected = stream.ReadInt32();

        QuestManager.Instance.TryCompleteQuestAsLetItDone(Connection.ActiveChar, _id, _objId, _selected);
    }
}
