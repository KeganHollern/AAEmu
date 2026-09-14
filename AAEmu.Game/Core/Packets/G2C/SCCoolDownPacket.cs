using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCCooldownsPacket : GamePacket
{
    // The r208022 client reserves 150 12-byte entries for each cooldown bucket.
    private const int MaximumEntriesPerBucket = 150;

    private readonly UnitCooldowns _cooldowns;

    public SCCooldownsPacket(UnitCooldowns cooldowns) : base(SCOffsets.SCCooldownsPacket, 1)
    {
        _cooldowns = cooldowns;
    }

    public override PacketStream Write(PacketStream stream)
    {
        var snapshot = _cooldowns.GetActiveBuckets(MaximumEntriesPerBucket);
        var skills = snapshot.Skills;

        stream.Write((uint)skills.Count);
        foreach (var skill in skills)
        {
            stream.Write(skill.Id);
            stream.Write(skill.Duration);
            stream.Write(skill.Remaining);
        }

        stream.Write((uint)snapshot.Tags.Count);
        foreach (var tag in snapshot.Tags)
        {
            stream.Write(tag.Id);
            stream.Write(tag.Duration);
            stream.Write(tag.Remaining);
        }

        return stream;
    }
}
