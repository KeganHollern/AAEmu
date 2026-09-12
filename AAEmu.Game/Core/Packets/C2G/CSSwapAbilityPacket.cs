using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSwapAbilityPacket() : GamePacket(CSOffsets.CSSwapAbilityPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (Connection.ActiveChar != null && TryReadRequest(stream, out var npcObjectId,
                out var oldAbility, out var newAbility, out _))
            Connection.ActiveChar.Abilities.Swap(oldAbility, newAbility, npcObjectId);
    }

    internal static bool TryReadRequest(PacketStream stream, out uint npcObjectId,
        out AbilityType oldAbility, out AbilityType newAbility, out bool autoUseAaPoints)
    {
        npcObjectId = 0;
        oldAbility = newAbility = AbilityType.None;
        autoUseAaPoints = false;
        if (stream.Count - stream.Pos != 6)
            return false;
        npcObjectId = stream.ReadBc();
        oldAbility = (AbilityType)stream.ReadByte();
        newAbility = (AbilityType)stream.ReadByte();
        var flag = stream.ReadByte();
        autoUseAaPoints = flag == 1;
        return flag <= 1;
    }
}
