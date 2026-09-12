using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Network.Stream;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Stream;

namespace AAEmu.Game.Core.Packets.C2S;

public class CTStartUploadEmblemStreamPacket() : StreamPacket(CTOffsets.CTStartUploadEmblemStreamPacket)
{
    public override void Read(PacketStream stream)
    {
        // The r208022 start body is a 15-byte header and a 52-byte UCC description.
        if (stream.LeftBytes != 67)
        {
            UccManager.Instance.FailUpload(Connection, ErrorMessageType.UccInvalidData);
            return;
        }
        try
        {
            var printerId = stream.ReadBc();
            var uccId = stream.ReadUInt64();
            var dataSize = stream.ReadInt32();
            if (uccId != 0 || dataSize < 0 || dataSize > UccUploadHandle.MaximumDdsSize || stream.LeftBytes != 52)
            {
                UccManager.Instance.FailUpload(Connection, ErrorMessageType.UccInvalidData);
                return;
            }
            DefaultUcc ucc = dataSize == 0 ? new DefaultUcc() : new CustomUcc();
            ucc.Read(stream);
            UccManager.Instance.StartUpload(Connection, printerId, dataSize, ucc);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            UccManager.Instance.FailUpload(Connection, ErrorMessageType.UccInvalidData);
        }
    }
}
