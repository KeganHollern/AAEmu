using System.Net;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Models;

namespace AAEmu.Game.Core.Packets.L2G;

public class LGPlayerEnterPacket() : LoginPacket(LGOffsets.LGPlayerEnterPacket)
{
    public override void Read(PacketStream stream)
    {
        if (TryReadAdmission(stream, out var account, out var connection, out var token,
                out var start, out var end, out var address))
            EnterWorldManager.Instance.AddAccount(account, connection, token, start, end, address);
    }

    internal static bool TryReadAdmission(PacketStream stream, out uint account, out uint connection,
        out uint token, out ulong start, out ulong end, out IPAddress address)
    {
        account = connection = token = 0;
        start = end = 0;
        address = null;
        if (stream.Count - stream.Pos != 44)
            return false;
        account = stream.ReadUInt32();
        connection = stream.ReadUInt32();
        token = stream.ReadUInt32();
        start = stream.ReadUInt64();
        end = stream.ReadUInt64();
        address = new IPAddress(stream.ReadBytes(16));
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return account != 0 && connection != 0 && token != 0 && AccountPayment.ValidPeriod(start, end)
            && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
    }
}
