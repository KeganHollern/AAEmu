using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCCooldownsPacketTests
{
    [Test]
    public async Task Write_ActiveSkills_WritesR208022CooldownLayout()
    {
        var cooldowns = new UnitCooldowns();
        cooldowns.AddCooldown(200, 60000);
        cooldowns.AddCooldown(100, 30000);

        var stream = new SCCooldownsPacket(cooldowns).Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadUInt32()).IsEqualTo(2u);
        await AssertSkill(stream, 100, 30000);
        await AssertSkill(stream, 200, 60000);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Write_NoActiveSkills_WritesBothEmptyCounts()
    {
        var stream = new SCCooldownsPacket(new UnitCooldowns()).Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    private static async Task AssertSkill(PacketStream stream, uint expectedSkillId, uint expectedDuration)
    {
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedSkillId);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedDuration);
        await Assert.That(stream.ReadUInt32()).IsGreaterThan(0u).And.IsLessThanOrEqualTo(expectedDuration);
    }
}
