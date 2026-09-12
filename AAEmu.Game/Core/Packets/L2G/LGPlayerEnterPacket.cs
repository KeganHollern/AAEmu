using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Models;

namespace AAEmu.Game.Core.Packets.L2G;

public class LGPlayerEnterPacket() : LoginPacket(LGOffsets.LGPlayerEnterPacket)
{
    public override void Read(PacketStream stream)
    {
        if (TryReadAdmission(stream, out var account, out var connection, out var start, out var end))
            EnterWorldManager.Instance.AddAccount(account, connection, start, end);
    }

    internal static bool TryReadAdmission(PacketStream stream, out uint account, out uint connection,
        out ulong start, out ulong end)
    {
        account = connection = 0;
        start = end = 0;
        if (stream.Count - stream.Pos != 24)
            return false;
        account = stream.ReadUInt32();
        connection = stream.ReadUInt32();
        start = stream.ReadUInt64();
        end = stream.ReadUInt64();
        return account != 0 && connection != 0 && AccountPayment.ValidPeriod(start, end);
    }
}
