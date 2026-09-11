using System.Text;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2L;
using AAEmu.Game.Core.Packets.L2G;
using AAEmu.Game.Models.Account;

namespace AAEmu.UnitTests.Game.Core.Packets;

public class ModerationPacketTests
{
    [Test]
    public async Task Request_MatchesGoPrivateProtocolGoldenBytes()
    {
        var request = new ModerationRequest(0x0102030405060708, 0x11223344, 0x55667788, 0x99aabbcc,
            ModerationAction.Ban, 60, "Aé");
        var body = new GLModerationRequestPacket(request).Write(new PacketStream());
        await Assert.That(Convert.ToHexString(body.GetBytes()).ToLowerInvariant())
            .IsEqualTo("08070605040302014433221188776655ccbbaa99013c00000000000000030041c3a9");
    }

    [Test]
    public async Task Result_ReadsGoPrivateProtocolGoldenBytes()
    {
        var body = new PacketStream().Write(Convert.FromHexString(
            "090000000000000000443322110807060504030201013c00000000000000010000000000000000"));
        body.Rollback();
        var result = LGModerationResultPacket.ReadBody(body);
        await Assert.That(result.RequestId).IsEqualTo(9ul);
        await Assert.That(result.State).IsEqualTo(new ModerationState(0x11223344, 0x0102030405060708, true, 60, true, 0));
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Request_WritesExactPrivateLayout()
    {
        var request = new ModerationRequest(0x0102030405060708, 10, 11, 20, ModerationAction.Mute, 3600, "réason");
        var packet = new GLModerationRequestPacket(request);
        var body = packet.Write(new PacketStream());
        body.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo((ushort)5);
        await Assert.That(body.Count).IsEqualTo(31 + Encoding.UTF8.GetByteCount(request.Reason));
        await Assert.That(body.ReadUInt64()).IsEqualTo(request.RequestId);
        await Assert.That(body.ReadUInt32()).IsEqualTo(10u);
        await Assert.That(body.ReadUInt32()).IsEqualTo(11u);
        await Assert.That(body.ReadUInt32()).IsEqualTo(20u);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)3);
        await Assert.That(body.ReadUInt64()).IsEqualTo(3600ul);
        await Assert.That(body.ReadString()).IsEqualTo("réason");
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task ReadState_WritesZeroActorAndEmptyReason()
    {
        var body = new GLModerationRequestPacket(new ModerationRequest(1, 0, 0, 20, ModerationAction.ReadState, 0, ""))
            .Write(new PacketStream());
        await Assert.That(body.Count).IsEqualTo(31);
    }

    [Test]
    [Arguments(256, true)]
    [Arguments(257, false)]
    public async Task Request_ReasonLimit_CountsUtf8Bytes(int characters, bool valid)
    {
        var request = new ModerationRequest(1, 10, 11, 20, ModerationAction.Ban, 0, new string('é', characters));
        await Assert.That(request.IsValid()).IsEqualTo(valid);
        if (!valid)
            await Assert.That(() => new GLModerationRequestPacket(request).Write(new PacketStream())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Request_InvalidFields_RejectBeforeSerialization()
    {
        var valid = new ModerationRequest(1, 10, 11, 20, ModerationAction.Ban, 1, "reason");
        var invalid = new[]
        {
            valid with { RequestId = 0 }, valid with { ActorAccountId = 0 }, valid with { ActorCharacterId = 0 },
            valid with { TargetAccountId = 0 }, valid with { Action = (ModerationAction)255 },
            valid with { Action = ModerationAction.Unban }, valid with { Reason = "\nreason" },
            valid with { Reason = "" }, valid with { DurationSeconds = ModerationRequest.MaximumDurationSeconds + 1 }
        };
        foreach (var request in invalid)
            await Assert.That(request.IsValid()).IsFalse();
    }

    [Test]
    public async Task Result_ReadsExactLayout()
    {
        var body = ResultBody();
        var result = LGModerationResultPacket.ReadBody(body);
        await Assert.That(body.Count).IsEqualTo(39);
        await Assert.That(result.RequestId).IsEqualTo(1ul);
        await Assert.That(result.Status).IsEqualTo(ModerationStatus.Success);
        await Assert.That(result.State).IsEqualTo(new ModerationState(20, 99, true, 1234, false, 0));
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task State_ReadsExactLayout()
    {
        var body = StateBody();
        var state = LGModerationStatePacket.ReadBody(body);
        await Assert.That(body.Count).IsEqualTo(30);
        await Assert.That(state).IsEqualTo(new ModerationState(20, 99, true, 1234, false, 0));
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Result_TruncationAtEveryByte_Rejects()
    {
        var bytes = ResultBody().GetBytes();
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = new PacketStream().Write(bytes[..length]);
            truncated.Rollback();
            await Assert.That(() => LGModerationResultPacket.ReadBody(truncated)).ThrowsException();
        }
    }

    [Test]
    public async Task State_TruncationAtEveryByte_Rejects()
    {
        var bytes = StateBody().GetBytes();
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = new PacketStream().Write(bytes[..length]);
            truncated.Rollback();
            await Assert.That(() => LGModerationStatePacket.ReadBody(truncated)).ThrowsException();
        }
    }

    [Test]
    public async Task ResultAndState_TrailingBytes_Reject()
    {
        var result = new PacketStream().Write(ResultBody().GetBytes()).Write((byte)1);
        result.Rollback();
        var state = new PacketStream().Write(StateBody().GetBytes()).Write((byte)1);
        state.Rollback();
        await Assert.That(() => LGModerationResultPacket.ReadBody(result)).Throws<InvalidDataException>();
        await Assert.That(() => LGModerationStatePacket.ReadBody(state)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 255)]
    public async Task Result_InvalidRequestOrStatus_Rejects(ulong requestId, byte status)
    {
        var body = ResultBody(requestId, status);
        await Assert.That(() => LGModerationResultPacket.ReadBody(body)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task State_InvalidBoolean_Rejects()
    {
        var body = new PacketStream().Write(20u).Write(1ul).Write((byte)2).Write(0ul).Write(false).Write(0ul);
        body.Rollback();
        await Assert.That(() => LGModerationStatePacket.ReadBody(body)).Throws<InvalidDataException>();
    }

    private static PacketStream ResultBody(ulong requestId = 1, byte status = 0)
    {
        var result = new PacketStream().Write(requestId).Write(status).Write(StateBody().GetBytes());
        result.Rollback();
        return result;
    }

    private static PacketStream StateBody()
    {
        var result = new PacketStream().Write(20u).Write(99ul).Write(true).Write(1234ul).Write(false).Write(0ul);
        result.Rollback();
        return result;
    }
}
