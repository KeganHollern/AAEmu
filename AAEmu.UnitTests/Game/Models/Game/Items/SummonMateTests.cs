using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

public sealed class SummonMateTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(255)]
    public async Task Details_ReadsNativeRepairByteAndWritesCanonicalSixByteState(int repairByte)
    {
        var native = new PacketStream().Write(123456).Write((byte)repairByte).Write((byte)42);
        var item = new SummonMate();
        item.ReadDetails(new PacketStream(native.GetBytes()));

        await Assert.That(item.DetailMateExp).IsEqualTo(123456);
        await Assert.That(item.DetailInjured).IsEqualTo(repairByte != 0);
        await Assert.That(item.DetailLevel).IsEqualTo((byte)42);
        await Assert.That(item.DetailType).IsEqualTo(ItemDetailType.Mate);
        await Assert.That(item.DetailBytesLength).IsEqualTo(6u);

        var output = new PacketStream();
        item.WriteDetails(output);
        await Assert.That(output.GetBytes().Length).IsEqualTo(6);
        var fields = new PacketStream(output.GetBytes());
        await Assert.That(fields.ReadInt32()).IsEqualTo(123456);
        await Assert.That(fields.ReadByte()).IsEqualTo(repairByte == 0 ? (byte)0 : (byte)1);
        await Assert.That(fields.ReadByte()).IsEqualTo((byte)42);
        await Assert.That(fields.LeftBytes).IsEqualTo(0);

        var restored = new SummonMate();
        restored.ReadDetails(new PacketStream(output.GetBytes()));
        await Assert.That(restored.DetailInjured).IsEqualTo(item.DetailInjured);
    }

    [Test]
    public async Task Details_TruncatedInputDoesNotPartiallyChangeTheSavedState()
    {
        var item = new SummonMate { DetailMateExp = 123456, DetailInjured = true, DetailLevel = 42 };
        item.ReadDetails(new PacketStream().Write(999).Write((byte)0));
        await Assert.That(item.DetailMateExp).IsEqualTo(123456);
        await Assert.That(item.DetailInjured).IsTrue();
        await Assert.That(item.DetailLevel).IsEqualTo((byte)42);
    }
}
