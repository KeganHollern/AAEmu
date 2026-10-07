using System.Net;
using System.Net.Sockets;
using System.Text;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Packets;

public sealed class GameUiDataLimitsTests
{
    [Test]
    [Arguments((ushort)1)]
    [Arguments((ushort)2)]
    [Arguments((ushort)3)]
    [Arguments((ushort)4)]
    [Arguments((ushort)5)]
    public async Task SaveUiData_ValidBodyStoresOnlyOwnCharacterAndConsumesAllBytes(ushort key)
    {
        var player = new CharacterMock { Id = 20, AccountId = 10 };
        var connection = new GameConnection(new RecordingSession());
        connection.TryAuthenticate(10);
        connection.ActiveChar = player;
        connection.State = GameState.World;
        var stream = Body(key, 20, Encoding.UTF8.GetBytes("layout"));
        new CSSaveUIDataPacket { Connection = connection }.Read(stream);
        await Assert.That(player.GetOption(key)).IsEqualTo("layout");
        await Assert.That(player.HasPendingUiData).IsTrue();
        await Assert.That(connection.IsClosed).IsFalse();
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task SaveUiData_WrongCharacterCannotChangeActiveUiState()
    {
        var player = new CharacterMock { Id = 20, AccountId = 10 };
        var connection = new GameConnection(new RecordingSession());
        connection.TryAuthenticate(10);
        connection.ActiveChar = player;
        new CSSaveUIDataPacket { Connection = connection }.Read(Body(5, 21, [65]));
        await Assert.That(player.GetOption(5)).IsEqualTo(string.Empty);
        await Assert.That(player.HasPendingUiData).IsFalse();
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task SaveUiData_UsesEncodedByteLimitAndAcceptsMaximum()
    {
        var maximum = Encoding.UTF8.GetBytes(new string('é', 4095) + "x");
        await Assert.That(maximum.Length).IsEqualTo(CharacterUiData.MaximumBytes);
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, maximum), out _, out _, out _)).IsTrue();
        var tooLong = Encoding.UTF8.GetBytes(new string('é', 4096));
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, tooLong), out _, out _, out _)).IsFalse();
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, []), out _, out _, out var empty)).IsTrue();
        await Assert.That(empty).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments((ushort)0)]
    [Arguments((ushort)6)]
    [Arguments((ushort)7)]
    [Arguments(ushort.MaxValue)]
    public async Task SaveUiData_LocalUnknownAndSentinelKeysAreRejected(ushort key)
    {
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(key, 20, []), out _, out _, out _)).IsFalse();
    }

    [Test]
    public async Task SaveUiData_TruncatedFieldsAndPayloadAreRejectedWithoutPartialState()
    {
        var full = Body(5, 20, [65, 66]).GetBytes();
        for (var length = 0; length < full.Length; ++length)
        {
            var stream = new PacketStream(full[..length]);
            await Assert.That(CSSaveUIDataPacket.TryReadData(stream, out _, out _, out var data)).IsFalse();
            await Assert.That(data).IsNull();
        }
    }

    [Test]
    public async Task SaveUiData_InvalidEncodingNulTrailingBytesAndZeroOwnerAreRejected()
    {
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, [0xc3]), out _, out _, out _)).IsFalse();
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, [65, 0, 66]), out _, out _, out _)).IsFalse();
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 0, []), out _, out _, out _)).IsFalse();
        var extra = Body(5, 20, [65]);
        extra.Pos = extra.Count;
        extra.Write((byte)0);
        extra.Rollback();
        await Assert.That(CSSaveUIDataPacket.TryReadData(extra, out _, out _, out _)).IsFalse();
    }

    private static PacketStream Body(ushort key, uint owner, byte[] bytes)
    {
        var stream = new PacketStream().Write(key).Write(owner).Write((ushort)bytes.Length).Write(bytes, false);
        stream.Rollback();
        return stream;
    }

    private sealed class RecordingSession : ISession
    {
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object value) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
