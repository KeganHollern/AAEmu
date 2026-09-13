using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Packets.G2L;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class WorldAdmissionOrderingTests
{
    [Test]
    public async Task Admission_OlderModerationResponse_DoesNotReplaceTheNewCookie()
    {
        var previousLogin = LoginNetwork.Instance.GetConnection();
        var session = new ReplySession();
        var moderation = new OrderedModeration();
        var accounts = Mock.Of<IAccountManager>();
        accounts.GetConnection(42).Returns((GameConnection)null);
        var manager = new EnterWorldManager(accounts.Object, Mock.Of<IStreamManager>().Object,
            Mock.Of<IQuestManager>().Object, Mock.Of<IChatManager>().Object, Mock.Of<IFamilyManager>().Object,
            Mock.Of<IWorldManager>().Object, moderation);
        LoginNetwork.Instance.SetConnection(new LoginConnection(session));
        try
        {
            manager.AddAccount(42, 100, 1, 0, 0, IPAddress.Loopback);
            manager.AddAccount(42, 200, 2, 0, 0, IPAddress.Loopback);
            moderation.Second.SetResult(true);
            await AssertReply(session, 200, 0);
            moderation.First.SetResult(true);
            await AssertReply(session, 100, 1);

            await Assert.That(manager.ConsumePendingAccount(1, 42)).IsEqualTo(PendingWorldAccountResult.NotFound);
            await Assert.That(manager.ConsumePendingAccount(2, 42)).IsEqualTo(PendingWorldAccountResult.Consumed);
        }
        finally
        {
            moderation.First.TrySetResult(false);
            moderation.Second.TrySetResult(false);
            LoginNetwork.Instance.SetConnection(previousLogin);
        }
    }

    private static async Task AssertReply(ReplySession session, uint correlation, byte result)
    {
        var bytes = await session.Replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var packet = new PacketStream(bytes);
        await Assert.That(packet.ReadUInt16()).IsEqualTo((ushort)8);
        await Assert.That(packet.ReadUInt16()).IsEqualTo(GLOffsets.GLPlayerEnterPacket);
        await Assert.That(packet.ReadUInt32()).IsEqualTo(correlation);
        packet.ReadByte(); // Configured Game server ID.
        await Assert.That(packet.ReadByte()).IsEqualTo(result);
        await Assert.That(packet.LeftBytes).IsEqualTo(0);
    }

    private sealed class OrderedModeration : IModerationManager
    {
        private int _requests;
        internal TaskCompletionSource<bool> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> RefreshAccountAsync(uint accountId) => Interlocked.Increment(ref _requests) == 1 ? First.Task : Second.Task;
        public bool TryAdmit(uint accountId, Action admit) { admit(); return true; }
        public Task<ModerationResult> ModerateAsync(Character actor, ModerationTarget target, ModerationAction action,
            ulong durationSeconds, string reason) => throw new NotSupportedException();
        public Task RefreshLiveAccountsAsync() => throw new NotSupportedException();
        public bool CanChat(uint accountId) => throw new NotSupportedException();
        public bool TryKick(Character actor, ModerationTarget target, string reason, out string error) => throw new NotSupportedException();
        public void CompleteRequest(ModerationResult result) => throw new NotSupportedException();
        public void ApplyState(ModerationState state) => throw new NotSupportedException();
        public bool TryResolveTarget(string value, out ModerationTarget target) => throw new NotSupportedException();
    }

    private sealed class ReplySession : ISession
    {
        internal Channel<byte[]> Replies { get; } = Channel.CreateUnbounded<byte[]>();
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Replies.Writer.TryWrite(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
