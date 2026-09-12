using System.Numerics;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCShowCommonFarmPacket : GamePacket
{
    public const int MaxPositions = 128;
    private readonly uint _farmId;
    private readonly Vector3[] _positions;

    public SCShowCommonFarmPacket(uint farmId, IEnumerable<Vector3> positions)
        : base(SCOffsets.SCShowCommonFarmPacket, 1)
    {
        _farmId = farmId;
        _positions = positions.Take(MaxPositions).ToArray();
    }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(_farmId);
        stream.Write(_positions.Length);
        foreach (var position in _positions)
            stream.WritePosition(position);
        return stream;
    }

}
