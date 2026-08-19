using System.Reflection;

using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.UnitTests.Utils.Mocks;

/// <summary>
/// Npc that records the unit movement it broadcasts, so tests can assert what the client would receive.
/// </summary>
public class MovementProbeNpc : Npc
{
    // Primary-constructor captures are unspeakable field names, so match on the field type
    private static readonly FieldInfo MoveTypeField = typeof(SCOneUnitMovementPacket)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(f => typeof(MoveType).IsAssignableFrom(f.FieldType));

    public List<UnitMoveType> Movements { get; } = [];

    public override void BroadcastPacket(GamePacket packet, bool self)
    {
        if (packet is not SCOneUnitMovementPacket)
            return;

        if (MoveTypeField.GetValue(packet) is UnitMoveType moveType)
            Movements.Add(moveType);
    }
}
