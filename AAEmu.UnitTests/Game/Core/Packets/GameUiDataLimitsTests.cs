using System.Net;
using System.Net.Sockets;
using System.Text;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
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
    [Arguments((byte)0x80)]
    [Arguments((byte)0xff)]
    public async Task SaveUiData_QuotedNativeTrackerMarkerAndChoicesRoundTrip(byte marker)
    {
        const string options = "\"\r\nquest_context_state_values\r\n    checkBoxStates ( 3094 false, 1313 true )\r\nversion 1\r\n";
        byte[] document = [(byte)'"', marker, .. Encoding.UTF8.GetBytes(options)];
        var player = new CharacterMock { Id = 37, AccountId = 10 };
        var connection = new GameConnection(new RecordingSession());
        connection.TryAuthenticate(10);
        connection.ActiveChar = player;
        connection.State = GameState.World;
        new CSSaveUIDataPacket { Connection = connection }.Read(Body(5, player.Id, document));
        await Assert.That(player.HasPendingUiData).IsTrue();

        var response = new SCResponseUIDataPacket(player.Id, 5, player.GetOptionForClient(5)).Write(new PacketStream());
        response.Rollback();
        await Assert.That(response.ReadUInt32()).IsEqualTo(player.Id);
        await Assert.That(response.ReadUInt16()).IsEqualTo((ushort)5);
        await Assert.That(response.ReadBytes().SequenceEqual(document)).IsTrue();
        await Assert.That(response.ReadInt32()).IsEqualTo(document.Length + 1);
        await Assert.That(response.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task RequestUiData_FullTrackerPreservesStoredChoiceWhenAnotherDefaultWillNotFit()
    {
        var player = new CharacterMock();
        player.Quests = new CharacterQuests(player);
        player.Quests.ActiveQuests[1313] = new Quest(null, player, null, null, null, null, null, initializeQuestActs: false);
        const string prefix = "quest_context_state_values\r\n    checkBoxStates ( 3094 false )\r\n";
        var value = prefix.PadRight(CharacterUiData.MaximumBytes, ' ');
        player.SetOption(5, value);
        await Assert.That(player.GetOptionForClient(5)).IsEqualTo(value);
    }

    [Test]
    public async Task SaveUiData_NativeMarkerOnlyAppliesToTrackerHeader()
    {
        byte[] header = [0x22, 0x80, 0x22, 13, 10];
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(1, 20, header), out _, out _, out _)).IsFalse();
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, [.. header, 0xff]), out _, out _, out _)).IsFalse();
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, [.. header, 0]), out _, out _, out _)).IsFalse();
        const string unicode = "\"é\"\r\nversion 1\r\n";
        var bytes = Encoding.UTF8.GetBytes(unicode);
        await Assert.That(CSSaveUIDataPacket.TryReadData(Body(5, 20, bytes), out _, out _, out var stored)).IsTrue();
        await Assert.That(stored).IsEqualTo(unicode);
        var response = new SCResponseUIDataPacket(20, 5, stored).Write(new PacketStream());
        response.Pos = sizeof(uint) + sizeof(ushort);
        await Assert.That(response.ReadBytes().SequenceEqual(bytes)).IsTrue();
        await Assert.That(response.ReadInt32()).IsEqualTo(bytes.Length + 1);
        await Assert.That(response.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task SaveUiData_UnreadableOwnedDocumentKeepsLastOptionAndCharacterSwitchAvailable()
    {
        var player = new CharacterMock { Id = 40, AccountId = 10 };
        player.SetOption(5, "last saved selection");
        var connection = new GameConnection(new RecordingSession());
        connection.TryAuthenticate(10);
        connection.ActiveChar = player;
        connection.State = GameState.World;

        var packet = new CSSaveUIDataPacket { Connection = connection };
        packet.Read(Body(5, player.Id, [65, 0, 66]));
        await Assert.That(connection.IsClosed).IsFalse();
        await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.CSLeaveWorldPacket, 1)).IsTrue();
        await Assert.That(player.GetOption(5)).IsEqualTo("last saved selection");
        await Assert.That(player.HasPendingUiData).IsFalse();

        packet.Read(Body(5, player.Id, Encoding.UTF8.GetBytes("next selection")));
        await Assert.That(player.GetOption(5)).IsEqualTo("next selection");
        await Assert.That(player.HasPendingUiData).IsTrue();
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
