using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCDoodadPhaseChangedPacket : GamePacket
{
    private readonly uint _templateId;
    private readonly uint _objId;
    private readonly uint _funcGroupId;
    private readonly uint _timeLeft;
    private readonly uint _itemTemplateId;

    public SCDoodadPhaseChangedPacket(Doodad doodad) : base(SCOffsets.SCDoodadPhaseChangedPacket, 1)
    {
        _templateId = doodad.TemplateId;
        _objId = doodad.ObjId;
        _funcGroupId = doodad.FuncGroupId;
        _timeLeft = doodad.TimeLeft;
        _itemTemplateId = doodad.ItemTemplateId;
        Logger.Trace("[Doodad] [0] SCDoodadPhaseChangedPacket: TemplateId {0}, ObjId {1},  CurrentPhaseId {2}, TimeLeft {3}", _templateId, _objId, _funcGroupId, _timeLeft);
    }

    public override PacketStream Write(PacketStream stream)
    {
        Logger.Debug("[Doodad] [2] SCDoodadPhaseChangedPacket: TemplateId {0}, ObjId {1},  CurrentPhaseId {2}, TimeLeft {3}", _templateId, _objId, _funcGroupId, _timeLeft);

        stream.WriteBc(_objId);
        stream.Write(_funcGroupId);
        stream.Write(_timeLeft); // growing
        stream.Write(-1); // puzzleGroup
        stream.Write(_itemTemplateId); // type(id) for backpack e.g. Id=27606 Sturgeon Pack
        return stream;
    }
}
