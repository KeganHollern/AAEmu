using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSStartQuestContextPacket() : GamePacket(CSOffsets.CSStartQuestContextPacket, 1)
{
    private uint _questContextId;
    private uint _npcObjId;
    private uint _doodadObjId;
    private uint _sphereId;

    public override void Read(PacketStream stream)
    {
        _questContextId = stream.ReadUInt32(); // questContextId
        _npcObjId = stream.ReadBc();           // npcObjId
        _doodadObjId = stream.ReadBc();        // doodadObjId
        _sphereId = stream.ReadUInt32();       // selected

        // Sphere starts come only from SphereQuestManager's authoritative entry event.
        // Do not let another supplied source hide a spoofed sphere or ambiguous object pair.
        if (_sphereId != 0 || (_npcObjId != 0 && _doodadObjId != 0))
            return;

        Connection.ActiveChar.Quests.AddQuestFromClient(_questContextId, _npcObjId, _doodadObjId);
    }
}
