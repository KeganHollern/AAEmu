using System.Net;
using System.Net.Sockets;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Packets;

public sealed class GameSessionStateTests
{
    [Test]
    [Arguments(CSOffsets.CSListCharacterPacket)]
    [Arguments(CSOffsets.CSRefreshInCharacterListPacket)]
    [Arguments(CSOffsets.CSCreateCharacterPacket)]
    [Arguments(CSOffsets.CSEditCharacterPacket)]
    [Arguments(CSOffsets.CSDeleteCharacterPacket)]
    [Arguments(CSOffsets.CSSelectCharacterPacket)]
    [Arguments(CSOffsets.CSCancelCharacterDeletePacket)]
    public async Task LobbyPackets_AllOtherStatesRejectBeforeDecode(ushort opcode)
    {
        foreach (var state in Enum.GetValues<GameState>().Where(state => state != GameState.Lobby))
        {
            var (connection, session) = MakeConnection(state);
            var handler = new GameProtocolHandler();
            handler.RegisterPacket(opcode, 1, typeof(ProbePacket));
            var frame = Frame(opcode);
            handler.OnReceive(connection, frame, 0, frame.Length);
            await Assert.That(session.Closes).IsEqualTo(1);
            await Assert.That(connection.GetAttribute("decoded")).IsNull();
        }
    }

    [Test]
    [Arguments(CSOffsets.CSStartSkillPacket)]
    [Arguments(CSOffsets.CSMoveUnitPacket)]
    [Arguments(CSOffsets.CSSwapItemsPacket)]
    [Arguments(CSOffsets.CSSendMailPacket)]
    [Arguments(CSOffsets.CSStartTradePacket)]
    [Arguments(CSOffsets.CSSendChatMessagePacket)]
    [Arguments(CSOffsets.CSCancelLeaveWorldPacket)]
    public async Task WorldPackets_RequireWorldAndOwnedCharacter(ushort opcode)
    {
        foreach (var state in Enum.GetValues<GameState>())
        {
            var (connection, _) = MakeConnection(state);
            await Assert.That(GameProtocolHandler.CanDispatch(connection, opcode, 1)).IsEqualTo(state == GameState.World);
            connection.ActiveChar = null;
            await Assert.That(GameProtocolHandler.CanDispatch(connection, opcode, 1)).IsFalse();
        }
    }

    [Test]
    public async Task WorldEntry_AdvancesOnceAndReturnsToAnEmptyLobby()
    {
        var (connection, _) = MakeConnection(GameState.Lobby);
        var first = new CharacterMock { AccountId = 20, Id = 1 };
        var second = new CharacterMock { AccountId = 20, Id = 2 };
        await Assert.That(connection.TrySelectCharacter(first)).IsTrue();
        await Assert.That(connection.TrySelectCharacter(second)).IsFalse();
        await Assert.That(connection.TryAdvanceWorldEntry(GameState.EnteringWorld, GameState.World)).IsFalse();
        await Assert.That(connection.TryAdvanceWorldEntry(GameState.CharacterSelected, GameState.EnteringWorld)).IsTrue();
        await Assert.That(connection.TryAdvanceWorldEntry(GameState.CharacterSelected, GameState.EnteringWorld)).IsFalse();
        await Assert.That(connection.TryAdvanceWorldEntry(GameState.EnteringWorld, GameState.World)).IsTrue();
        await Assert.That(connection.TryAdvanceWorldEntry(GameState.EnteringWorld, GameState.World)).IsFalse();
        await Assert.That(connection.TryCompleteWorldEntry()).IsTrue();
        await Assert.That(connection.TryCompleteWorldEntry()).IsFalse();
        connection.ReturnToLobby();
        await Assert.That(connection.ActiveChar).IsNull();
        await Assert.That(connection.TrySelectCharacter(second)).IsTrue();
    }

    [Test]
    public async Task EntryPackets_RejectReplaysBeforeAnyCharacterAction()
    {
        foreach (var packet in new GamePacket[] { new CSSelectCharacterPacket(), new CSSpawnCharacterPacket() })
        {
            var (connection, session) = MakeConnection(GameState.World);
            connection.TryCompleteWorldEntry();
            var character = connection.ActiveChar;
            packet.Connection = connection;
            packet.Decode(new PacketStream());
            await Assert.That(session.Closes).IsEqualTo(1);
            await Assert.That(connection.ActiveChar).IsSameReferenceAs(character);
        }
    }

    [Test]
    public async Task RepeatedNotifyInGame_DoesNotSpawnOrDisconnectTheWorldSession()
    {
        var (connection, session) = MakeConnection(GameState.World);
        var character = connection.ActiveChar;
        var packet = new CSNotifyInGamePacket { Connection = connection };
        packet.Decode(new PacketStream());
        packet.Decode(new PacketStream());
        await Assert.That(session.Closes).IsEqualTo(0);
        await Assert.That(connection.ActiveChar).IsSameReferenceAs(character);
        // Spawn's first action marks the character online. A replay must not reach it.
        await Assert.That(character.IsOnline).IsFalse();
    }

    [Test]
    public async Task WorldEntryPackets_AllowOnlyTheirExpectedState()
    {
        foreach (var state in Enum.GetValues<GameState>())
        {
            var (connection, _) = MakeConnection(state);
            await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.CSSpawnCharacterPacket, 1))
                .IsEqualTo(state == GameState.CharacterSelected);
            await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.CSNotifyInGamePacket, 1))
                .IsEqualTo(state is GameState.EnteringWorld or GameState.World);
            await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.CSNotifyInGameCompletedPacket, 1))
                .IsEqualTo(state == GameState.World);
        }
    }

    [Test]
    [Arguments(CSOffsets.CSInstanceLoadedPacket)]
    [Arguments(CSOffsets.CSNotifySubZonePacket)]
    public async Task InstanceLoadPackets_AllowOwnedCharacterDuringLoadingAndWorld(ushort opcode)
    {
        foreach (var state in Enum.GetValues<GameState>())
        {
            var (connection, _) = MakeConnection(state);
            await Assert.That(GameProtocolHandler.CanDispatch(connection, opcode, 1)).IsEqualTo(
                state is GameState.CharacterSelected or GameState.EnteringWorld or GameState.World);
            if (connection.ActiveChar != null)
                connection.ActiveChar.AccountId = 999;
            await Assert.That(GameProtocolHandler.CanDispatch(connection, opcode, 1)).IsFalse();
        }
    }

    [Test]
    public async Task ParallelCharacterSelection_OnlyOneCharacterBinds()
    {
        var (connection, _) = MakeConnection(GameState.Lobby);
        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(id => Task.Run(() =>
            connection.TrySelectCharacter(new CharacterMock { AccountId = 20, Id = (uint)id }))));
        await Assert.That(results.Count(accepted => accepted)).IsEqualTo(1);
        await Assert.That(connection.State).IsEqualTo(GameState.CharacterSelected);
    }

    [Test]
    public async Task Disconnect_CleanupFailureStillSavesRemovesAndStopsPackets()
    {
        var (connection, session) = MakeConnection(GameState.World);
        var calls = new List<string>();
        connection.CompleteDisconnect(() => throw new IOException("chat cleanup failed"),
            () => calls.Add("save"), () => calls.Add("remove"));
        connection.CompleteDisconnect(() => calls.Add("repeat"), () => calls.Add("repeat"), () => calls.Add("repeat"));
        connection.Shutdown();
        await Assert.That(string.Join(',', calls)).IsEqualTo("save,remove");
        await Assert.That(connection.ActiveChar).IsNull();
        await Assert.That(connection.IsClosed).IsTrue();
        await Assert.That(connection.DisconnectSaveSucceeded).IsTrue();
        await Assert.That(session.Closes).IsEqualTo(1);
        await Assert.That(GameProtocolHandler.CanDispatch(connection, CSOffsets.CSListCharacterPacket, 1)).IsFalse();
    }

    [Test]
    public async Task Disconnect_SaveFailureStillRemovesButDoesNotReportSaveSuccess()
    {
        var (connection, _) = MakeConnection(GameState.World);
        var removed = false;
        var result = connection.CompleteDisconnect(() => { }, () => throw new IOException("save failed"), () => removed = true);
        await Assert.That(result).IsFalse();
        await Assert.That(removed).IsTrue();
        await Assert.That(connection.ActiveChar).IsNull();
    }

    [Test]
    public async Task Disconnect_ParallelCallsWaitForOneSave()
    {
        var (connection, _) = MakeConnection(GameState.World);
        var saves = 0;
        var removals = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => connection.CompleteDisconnect(
            () => { }, () => Interlocked.Increment(ref saves), () => Interlocked.Increment(ref removals)))));
        await Assert.That(saves).IsEqualTo(1);
        await Assert.That(removals).IsEqualTo(1);
    }

    private static (GameConnection, RecordingSession) MakeConnection(GameState state)
    {
        var session = new RecordingSession();
        var connection = new GameConnection(session);
        if (state != GameState.Connected)
            connection.TryAuthenticate(20);
        connection.State = state;
        if (state is GameState.CharacterSelected or GameState.EnteringWorld or GameState.World)
            connection.ActiveChar = new CharacterMock { AccountId = 20, Id = 1 };
        return (connection, session);
    }

    private static byte[] Frame(ushort opcode) => new PacketStream().Write(new PacketStream()
        .Write((byte)0).Write((byte)1).Write((byte)0).Write((byte)0).Write(opcode)).GetBytes();

    public sealed class ProbePacket() : GamePacket(CSOffsets.CSCreateCharacterPacket, 1)
    {
        public override void Read(PacketStream stream) => Connection.AddAttribute("decoded", true);
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public int Closes { get; private set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object value) => _attributes[name] = value;
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() => Closes++;
    }
}
