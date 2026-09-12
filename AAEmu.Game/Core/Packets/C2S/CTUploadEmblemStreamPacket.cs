using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Network.Stream;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Stream;

namespace AAEmu.Game.Core.Packets.C2S;

public class CTUploadEmblemStreamPacket() : StreamPacket(CTOffsets.CTUploadEmblemStreamPacket)
{
    public override void Read(PacketStream stream)
    {
        if (stream.LeftBytes < 14)
        {
            UccManager.Instance.FailUpload(Connection, ErrorMessageType.UccInvalidData);
            return;
        }
        var total = stream.ReadInt32();
        var size = stream.ReadInt32();
        var index = stream.ReadUInt32();
        var partSize = stream.ReadUInt16();
        if (size != partSize || partSize is 0 or > UccUploadHandle.PartSize || stream.LeftBytes != partSize)
        {
            UccManager.Instance.FailUpload(Connection, ErrorMessageType.UccInvalidData);
            return;
        }
        UccManager.Instance.UploadPart(Connection, new UccPart
        {
            Total = total, Size = size, Index = index, Data = stream.ReadBytes(partSize)
        });
    }
}
