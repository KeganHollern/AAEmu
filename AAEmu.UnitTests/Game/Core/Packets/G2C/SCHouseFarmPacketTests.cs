using System.Text;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class SCHouseFarmPacketTests
{
    [Test]
    [Arguments("", 0, 0)]
    [Arguments("Pine", 9, 3)]
    [Arguments("소나무", 7, 1)]
    public async Task Write_ChatNotice_UsesNameThenTotalThenHarvestable(string name, int total, int harvestable)
    {
        var stream = new SCHouseFarmPacket(name, total, harvestable).Write(new PacketStream());
        stream.Rollback();

        var byteCount = stream.ReadUInt16();
        await Assert.That(byteCount).IsEqualTo((ushort)Encoding.UTF8.GetByteCount(name));
        await Assert.That(Encoding.UTF8.GetString(stream.ReadBytes(byteCount))).IsEqualTo(name);
        await Assert.That(stream.ReadInt32()).IsEqualTo(total);
        await Assert.That(stream.ReadInt32()).IsEqualTo(harvestable);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Write_NativeNameCapacity_KeepsTheCountersAfterAllNameBytes()
    {
        var name = new string('n', 128);
        var stream = new SCHouseFarmPacket(name, int.MaxValue, 1).Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadString()).IsEqualTo(name);
        await Assert.That(stream.ReadInt32()).IsEqualTo(int.MaxValue);
        await Assert.That(stream.ReadInt32()).IsEqualTo(1);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }
}
