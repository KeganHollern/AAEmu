using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

public sealed class VehicleRepairTests
{
    private static readonly DateTime Started = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task GetSpawnError_DestroyedWithoutRepair_NeverExpires()
    {
        var item = new SummonSlave { IsDestroyed = 1 };
        await Assert.That(item.GetSpawnError(Started, out var remaining))
            .IsEqualTo(ErrorMessageType.SlaveSpawnErrorDestroyed);
        await Assert.That(remaining).IsEqualTo(0u);
        await Assert.That(item.GetSpawnError(Started.AddYears(1), out _))
            .IsEqualTo(ErrorMessageType.SlaveSpawnErrorDestroyed);
    }

    [Test]
    [Arguments(0d, 600u, ErrorMessageType.SlaveSpawnErrorNeedRepairTime)]
    [Arguments(0.001d, 600u, ErrorMessageType.SlaveSpawnErrorNeedRepairTime)]
    [Arguments(599d, 1u, ErrorMessageType.SlaveSpawnErrorNeedRepairTime)]
    [Arguments(599.999d, 1u, ErrorMessageType.SlaveSpawnErrorNeedRepairTime)]
    [Arguments(600d, 0u, ErrorMessageType.SlaveSpawnErrorNeedRepairTime)]
    [Arguments(600.999d, 0u, ErrorMessageType.SlaveSpawnErrorNeedRepairTime)]
    [Arguments(601d, 0u, ErrorMessageType.NoErrorMessage)]
    public async Task GetSpawnError_RepairBoundary_UsesCompleteTenMinuteWait(
        double elapsed, uint expectedRemaining, ErrorMessageType expectedError)
    {
        var item = new SummonSlave { RepairStartTime = Started };
        await Assert.That(item.GetSpawnError(Started.AddSeconds(elapsed), out var remaining)).IsEqualTo(expectedError);
        await Assert.That(remaining).IsEqualTo(expectedRemaining);
    }

    [Test]
    public async Task GetSpawnError_UnbrokenItem_DoesNotNeedRepair()
    {
        var item = new SummonSlave();
        await Assert.That(item.GetSpawnError(Started, out var remaining)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(remaining).IsEqualTo(0u);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Details_WritesNativeFixedLengthAndRoundTripsRepairState(bool repairing)
    {
        var item = new SummonSlave
        {
            SlaveType = 2, SlaveDbId = 0xabcdef,
            RepairStartTime = repairing ? Started : DateTime.MinValue,
            IsDestroyed = repairing ? (byte)0 : (byte)1
        };
        var output = new PacketStream();
        item.WriteDetails(output);
        await Assert.That(output.GetBytes().Length).IsEqualTo(29);

        var fields = new PacketStream(output.GetBytes());
        await Assert.That(fields.ReadByte()).IsEqualTo((byte)2);
        await Assert.That(fields.ReadBc()).IsEqualTo(0xabcdefu);
        await Assert.That(fields.ReadByte()).IsEqualTo(item.IsDestroyed);
        await Assert.That(fields.ReadInt64()).IsEqualTo(repairing ? Helpers.UnixTime(Started) : 0L);
        await Assert.That(fields.ReadBytes(16)).IsEquivalentTo(new byte[16]);
        await Assert.That(fields.LeftBytes).IsEqualTo(0);

        var restored = new SummonSlave();
        var input = new PacketStream(output.GetBytes());
        restored.ReadDetails(input);
        await Assert.That(input.LeftBytes).IsEqualTo(0);
        await Assert.That(restored.SlaveType).IsEqualTo(item.SlaveType);
        await Assert.That(restored.SlaveDbId).IsEqualTo(item.SlaveDbId);
        await Assert.That(restored.IsDestroyed).IsEqualTo(item.IsDestroyed);
        await Assert.That(restored.RepairStartTime).IsEqualTo(item.RepairStartTime);
        await Assert.That(restored.GetSpawnError(Started.AddSeconds(599), out _)).IsEqualTo(
            repairing ? ErrorMessageType.SlaveSpawnErrorNeedRepairTime : ErrorMessageType.SlaveSpawnErrorDestroyed);
    }

    [Test]
    public async Task Details_ReadDoesNotInventMeaningForLocationFields()
    {
        var location = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var native = new PacketStream();
        native.Write((byte)2).WriteBc(123).Write((byte)0).Write(Helpers.UnixTime(Started)).Write(location);
        var item = new SummonSlave();
        item.ReadDetails(new PacketStream(native.GetBytes()));
        var output = new PacketStream();
        item.WriteDetails(output);
        await Assert.That(output.GetBytes()).IsEquivalentTo(native.GetBytes());
    }
}
