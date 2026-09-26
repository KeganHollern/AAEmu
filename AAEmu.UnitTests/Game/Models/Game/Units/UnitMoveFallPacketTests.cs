using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed class UnitMoveFallPacketTests
{
    [Test]
    [Arguments((byte)0, (ushort)0)]
    [Arguments((byte)0x80, (ushort)1)]
    [Arguments((byte)0x80, ushort.MaxValue)]
    public async Task NativeOptionalFallField_ConsumesOnlyWhenFlagIsSet(byte flags, ushort speed)
    {
        var move = new UnitMoveType { ActorFlags = flags, FallVel = speed, DeltaMovement = [0, 0, 0] };
        var stream = move.Write(new PacketStream()).Write(0x11223344u);
        var received = new UnitMoveType();
        received.Read(stream);
        await Assert.That(received.ActorFlags).IsEqualTo(flags);
        await Assert.That(received.FallVel).IsEqualTo(speed);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(0x11223344u);
    }

    [Test]
    public async Task TruncatedOptionalFallField_FailsBeforeMovementCanApply()
    {
        var bytes = new UnitMoveType { ActorFlags = 0x80, FallVel = ushort.MaxValue, DeltaMovement = [0, 0, 0] }
            .Write(new PacketStream()).GetBytes();
        await Assert.That(() => new UnitMoveType().Read(new PacketStream().Write(bytes[..^1]))).ThrowsException();
    }
}
