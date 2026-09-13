using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class DuplicateSessionPersistenceTests : IDisposable
{
    private static readonly FieldInfo s_accountInstance = typeof(Singleton<AccountManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly AccountManager _previous = (AccountManager)s_accountInstance.GetValue(null);
    private readonly AccountManager _accounts = new(Mock.Of<ITickManager>(), Mock.Of<ITimedRewardsManager>(), TimeProvider.System);

    public DuplicateSessionPersistenceTests() => s_accountInstance.SetValue(null, _accounts);

    public void Dispose() => s_accountInstance.SetValue(null, _previous);

    [Fact]
    public void DuplicateLogin_ClosesOldSessionAndAdmitsNewBeforeItsDelayedDisconnect()
    {
        var oldSocket = new RecordingSession(900001);
        var old = new GameConnection(oldSocket);
        Assert.True(old.TryAuthenticate(900001));
        _accounts.Add(old);
        var manager = CreateManager();

        Assert.True(manager.PreparePendingAccount(old.AccountId, 987654321, new AccountPayment(),
            IPAddress.Loopback, PermittedModeration()));
        Assert.True(old.IsClosed);
        Assert.Equal(1, oldSocket.Closes);
        Assert.Null(_accounts.GetConnection(old.AccountId));
        var kicked = Assert.Single(oldSocket.Packets);
        Assert.Equal(SCOffsets.SCKickedPacket, BitConverter.ToUInt16(kicked, 6));
        Assert.Equal((byte)KickedReason.KickDuplicateAccount, kicked[8]);
        Assert.Equal(PendingWorldAccountResult.Consumed, manager.ConsumePendingAccount(987654321, old.AccountId));

        var replacement = new GameConnection(new RecordingSession(900002));
        Assert.True(replacement.TryAuthenticate(old.AccountId));
        _accounts.Add(replacement);
        old.OnDisconnect();
        _accounts.Remove(old);
        Assert.Same(replacement, _accounts.GetConnection(old.AccountId));
        Assert.True(_accounts.IsCurrent(replacement));
    }

    [Fact]
    public async Task ConcurrentDuplicateLogins_CloseTheOldSocketOnce()
    {
        var socket = new RecordingSession(900003);
        var old = new GameConnection(socket);
        Assert.True(old.TryAuthenticate(900003));
        _accounts.Add(old);
        var manager = CreateManager();
        var moderation = PermittedModeration();

        var results = await Task.WhenAll(Enumerable.Range(1, 10).Select(id => Task.Run(() =>
            manager.PreparePendingAccount(old.AccountId, (uint)id, new AccountPayment(), IPAddress.Loopback, moderation))));

        Assert.All(results, Assert.True);
        Assert.Equal(1, socket.Closes);
        Assert.Null(_accounts.GetConnection(old.AccountId));

    }

    private EnterWorldManager CreateManager() => new(_accounts, Mock.Of<IStreamManager>(), Mock.Of<IQuestManager>(),
        Mock.Of<IChatManager>(), Mock.Of<IFamilyManager>(), Mock.Of<IWorldManager>());

    private static IModerationManager PermittedModeration()
    {
        var manager = new Mock<IModerationManager>();
        manager.Setup(value => value.TryAdmit(It.IsAny<uint>(), It.IsAny<Action>()))
            .Returns((uint _, Action admit) => { admit(); return true; });
        return manager.Object;
    }

    private sealed class RecordingSession(uint id) : ISession
    {
        public List<byte[]> Packets { get; } = [];
        public int Closes { get; private set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => id;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet);
        public void AddAttribute(string name, object value) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() => Closes++;
    }
}
