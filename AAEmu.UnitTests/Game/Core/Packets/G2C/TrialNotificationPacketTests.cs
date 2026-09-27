using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class TrialNotificationPacketTests
{
    [Test]
    [Arguments(0, 0)] [Arguments(1, 5)] [Arguments(5, 5)]
    public async Task VerdictCount_WritesTwoInt32Fields(int count, int total)
    {
        var packet = new SCChangeJuryVerdictCountPacket(count, total);
        var body = packet.Write(new PacketStream());
        body.Rollback();
        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x17a);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.Count).IsEqualTo(8);
        await Assert.That(body.ReadInt32()).IsEqualTo(count);
        await Assert.That(body.ReadInt32()).IsEqualTo(total);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(0u)] [Arguments(0x12345678u)] [Arguments(uint.MaxValue)]
    public async Task Cancellation_WritesOnlyTheTrialId(uint trialId)
    {
        var packet = new SCTrialCanceledPacket(trialId);
        var body = packet.Write(new PacketStream());
        body.Rollback();
        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x183);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.Count).IsEqualTo(4);
        await Assert.That(body.ReadUInt32()).IsEqualTo(trialId);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(true, 0, 0)] [Arguments(true, 1, 4)]
    [Arguments(false, 0, 0)] [Arguments(false, 1, 4)]
    public async Task JurySeating_UsesLocalBankAndZeroBasedSeat(bool isWest, int bank, int seat)
    {
        var summon = new SCSummonJuryPacket(0x12345678, (uint)bank, seat);
        var summonBody = summon.Write(new PacketStream());
        summonBody.Rollback();
        await Assert.That(summon.TypeId).IsEqualTo((ushort)0x173);
        await Assert.That(summonBody.Count).IsEqualTo(12);
        await Assert.That(summonBody.ReadUInt32()).IsEqualTo(0x12345678u);
        await Assert.That(summonBody.ReadUInt32()).IsEqualTo((uint)bank);
        await Assert.That(summonBody.ReadInt32()).IsEqualTo(seat);
        await Assert.That(summonBody.LeftBytes).IsEqualTo(0);

        var seated = new SCJuryBeSeatedPacket(isWest, 0x12345678, bank, seat);
        var seatedBody = seated.Write(new PacketStream());
        seatedBody.Rollback();
        await Assert.That(seated.TypeId).IsEqualTo((ushort)0x174);
        await Assert.That(seatedBody.Count).IsEqualTo(13);
        await Assert.That(seatedBody.ReadBoolean()).IsEqualTo(isWest);
        await Assert.That(seatedBody.ReadUInt32()).IsEqualTo(0x12345678u);
        await Assert.That(seatedBody.ReadInt32()).IsEqualTo(bank);
        await Assert.That(seatedBody.ReadInt32()).IsEqualTo(seat);
        await Assert.That(seatedBody.LeftBytes).IsEqualTo(0);
    }

}
