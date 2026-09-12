using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Network.Stream;

namespace AAEmu.Game.Core.Packets.C2S;

public class CTEmblemStreamUploadStatusPacket() : StreamPacket(CTOffsets.CTEmblemStreamUploadStatusPacket)
{
    public override void Read(PacketStream stream)
    {
        if (stream.LeftBytes != 1)
        {
            UccManager.Instance.FailUpload(Connection, AAEmu.Game.Models.Game.ErrorMessageType.UccInvalidData);
            return;
        }
        var status = stream.ReadByte();

        UccManager.Instance.ConfirmDefaultUcc(Connection, status);
    }
}
