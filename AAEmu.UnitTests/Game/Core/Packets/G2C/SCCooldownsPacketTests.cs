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

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(150)]
    [Arguments(151)]
    public async Task Write_BothBuckets_UsesIndependentNativeLimits(int count)
    {
        var cooldowns = new UnitCooldowns();
        for (var id = count; id > 0; id--)
        {
            cooldowns.AddCooldown((uint)id, 60000);
            cooldowns.AddCooldown((uint)(id + 1000), 90000, id);
        }
        var stream = new SCCooldownsPacket(cooldowns).Write(new PacketStream());
        stream.Rollback();
        var expected = (uint)Math.Min(count, 150);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expected);
        for (uint id = 1; id <= expected; id++)
            await AssertSkill(stream, id, 60000);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expected);
        for (uint id = 1; id <= expected; id++)
            await AssertSkill(stream, id, 90000);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Write_BothBuckets_UsesOneTimestampAndRetainsMaximumDuration()
    {
        var clock = new AAEmu.UnitTests.Game.Models.Game.Units.SharedCooldownTests.ManualTimeProvider();
        var cooldowns = new UnitCooldowns(clock);
        cooldowns.AddCooldown(200, uint.MaxValue);
        cooldowns.AddCooldown(11715, uint.MaxValue, 30);
        clock.Advance(123);
        var stream = new SCCooldownsPacket(cooldowns).Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.ReadUInt32()).IsEqualTo(1u);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(200u);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(uint.MaxValue);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(uint.MaxValue - 123);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(1u);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(30u);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(uint.MaxValue);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(uint.MaxValue - 123);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    private static async Task AssertSkill(PacketStream stream, uint expectedSkillId, uint expectedDuration)
    {
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedSkillId);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedDuration);
        await Assert.That(stream.ReadUInt32()).IsGreaterThan(0u).And.IsLessThanOrEqualTo(expectedDuration);
    }
}
