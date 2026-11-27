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
                out var start, out var end, out var address, out var cap, out var permitReservation))
            EnterWorldManager.Instance.AddAccount(account, connection, token, start, end, address, cap, permitReservation);
    }

    internal static bool TryReadAdmission(PacketStream stream, out uint account, out uint connection,
        out uint token, out ulong start, out ulong end, out IPAddress address, out ushort populationCap, out bool permitReservation)
    {
        account = connection = token = 0;
        start = end = 0;
        address = null;
        populationCap = 0;
        permitReservation = true;
        var length = stream.LeftBytes;
        if (length is not (44 or 47))
            return false;
        account = stream.ReadUInt32();
        connection = stream.ReadUInt32();
        token = stream.ReadUInt32();
        start = stream.ReadUInt64();
        end = stream.ReadUInt64();
        address = new IPAddress(stream.ReadBytes(16));
        if (length == 47)
        {
            populationCap = stream.ReadUInt16();
            var permit = stream.ReadByte();
            if (populationCap == 0 || permit > 1)
                return false;
            permitReservation = permit == 1;
        }
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return account != 0 && connection != 0 && token != 0 && AccountPayment.ValidPeriod(start, end)
            && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
    }
}
