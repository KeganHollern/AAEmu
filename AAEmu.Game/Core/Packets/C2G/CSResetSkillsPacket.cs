using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSResetSkillsPacket() : GamePacket(CSOffsets.CSResetSkillsPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (Connection.ActiveChar != null && TryReadRequest(stream, out var ability, out _))
            Connection.ActiveChar.Skills.Reset(ability);
    }

    internal static bool TryReadRequest(PacketStream stream, out AbilityType ability, out bool autoUseAaPoints)
    {
        ability = AbilityType.None;
        autoUseAaPoints = false;
        if (stream.Count - stream.Pos != 2)
            return false;
        ability = (AbilityType)stream.ReadByte();
        var flag = stream.ReadByte();
        autoUseAaPoints = flag == 1;
        return flag <= 1;
    }
}
