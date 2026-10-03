using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Slaves;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSTurretStatePacket() : GamePacket(CSOffsets.CSTurretStatePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var (unitId, pitch, yaw) = ReadRequest(stream);
        TurretAimControl.TryApply(Connection.ActiveChar, unitId, pitch, yaw);
    }

    internal static (uint UnitId, float Pitch, float Yaw) ReadRequest(PacketStream stream)
    {
        if (stream.LeftBytes != 11)
            throw new MarshalException("CSTurretState requires its 11-byte r208022 body.");
        return (stream.ReadBc(), stream.ReadSingle(), stream.ReadSingle());
    }
}
