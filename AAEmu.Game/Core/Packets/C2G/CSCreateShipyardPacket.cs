using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Shipyard;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSCreateShipyardPacket() : GamePacket(CSOffsets.CSCreateShipyardPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var request = ReadRequest(stream);
        ShipyardManager.Instance.Create(Connection.ActiveChar, request);
    }

    internal static ShipyardPlacementRequest ReadRequest(PacketStream stream)
    {
        if (stream.LeftBytes != 61)
            throw new MarshalException("CSCreateShipyard requires its 61-byte r208022 body.");
        var id = stream.ReadUInt32();
        var x = Helpers.ConvertLongX(stream.ReadInt64());
        var y = Helpers.ConvertLongY(stream.ReadInt64());
        var z = stream.ReadSingle();
        var zRot = stream.ReadSingle();
        var designItem = stream.ReadUInt64();
        var mAABBmnX = stream.ReadSingle();
        var mAABBmnY = stream.ReadSingle();
        var mAABBmnZ = stream.ReadSingle();
        var mAABBmxX = stream.ReadSingle();
        var mAABBmxY = stream.ReadSingle();
        var mAABBmxZ = stream.ReadSingle();
        var autoUseAAPoint = stream.ReadBoolean();

        // r208022 397bc060: the bounds describe the preview model before its pose is applied.
        return new(id, new(x, y, z), zRot, designItem,
            new(new(mAABBmnX, mAABBmnY, mAABBmnZ), new(mAABBmxX, mAABBmxY, mAABBmxZ)), autoUseAAPoint);
    }
}
