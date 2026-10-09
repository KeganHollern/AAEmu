using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class MateMileagePacketTests
{
    [Test]
    [Arguments(0, 0x00, 0x00, 0x00, 0x00)]
    [Arguments(12345, 0x39, 0x30, 0x00, 0x00)]
    [Arguments(-12345, 0xc7, 0xcf, 0xff, 0xff)]
    [Arguments(int.MinValue, 0x00, 0x00, 0x00, 0x80)]
    [Arguments(int.MaxValue, 0xff, 0xff, 0xff, 0x7f)]
    public async Task MileageChanged_WritesPackedObjectIdAndSignedInt32Delta(
        int mileageDelta, int byte0, int byte1, int byte2, int byte3)
    {
        const uint objId = 0x563412;
        var packet = new SCMileageChangedPacket(objId, mileageDelta);
        var body = packet.Write(new PacketStream());
        body.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x101);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.Count).IsEqualTo(7);
        await Assert.That(body.ReadBc()).IsEqualTo(objId);
        await Assert.That(body.Pos).IsEqualTo(3);
        await Assert.That(body.ReadInt32()).IsEqualTo(mileageDelta);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(body.GetBytes().SequenceEqual(new byte[]
        {
            0x12, 0x34, 0x56, (byte)byte0, (byte)byte1, (byte)byte2, (byte)byte3
        })).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(123456789)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task MateSpawned_WritesSignedTotalMileageBeforeDelayAndTenSkillSlots(int totalMileage)
    {
        var mate = new Mate
        {
            TlId = 0x1234,
            Id = 0x11223344,
            ItemId = 0x0102030405060708,
            UserState = 0x5a,
            Experience = 0x12345678,
            Mileage = totalMileage,
            SpawnDelayTime = 0x99887766,
            Skills = []
        };
        var packet = new SCMateSpawnedPacket(mate);
        var body = packet.Write(new PacketStream());
        body.Rollback();

        await Assert.That(body.Count).IsEqualTo(67);
        await Assert.That(body.ReadUInt16()).IsEqualTo(mate.TlId);
        await Assert.That(body.ReadUInt32()).IsEqualTo(mate.Id);
        await Assert.That(body.ReadUInt64()).IsEqualTo(mate.ItemId);
        await Assert.That(body.ReadByte()).IsEqualTo(mate.UserState);
        await Assert.That(body.ReadInt32()).IsEqualTo(mate.Experience);
        await Assert.That(body.Pos).IsEqualTo(19);
        await Assert.That(body.ReadInt32()).IsEqualTo(totalMileage);
        await Assert.That(body.ReadUInt32()).IsEqualTo(mate.SpawnDelayTime);
        for (var slot = 0; slot < 10; slot++)
            await Assert.That(body.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }
}
