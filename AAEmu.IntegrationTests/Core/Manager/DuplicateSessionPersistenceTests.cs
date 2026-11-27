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
using AAEmu.Game.Models.Account;
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
    private readonly Mock<ITimedRewardsManager> _rewards = new();
    private readonly AccountManager _accounts;
    private readonly ModerationManager _moderation;
    private readonly AppConfiguration.NetworkConfig _network = AppConfiguration.Instance.Network;
    private readonly AppConfiguration.NetworkConfig _streamNetwork = AppConfiguration.Instance.StreamNetwork;

    public DuplicateSessionPersistenceTests()
    {
        _accounts = new AccountManager(Mock.Of<ITickManager>(), _rewards.Object, TimeProvider.System);
        _moderation = new ModerationManager(new PermissionManager(_accounts));
        s_accountInstance.SetValue(null, _accounts);
        AppConfiguration.Instance.Network = new AppConfiguration.NetworkConfig();
        AppConfiguration.Instance.StreamNetwork = new AppConfiguration.NetworkConfig { Port = 1250 };
    }

    public void Dispose()
    {
        AppConfiguration.Instance.Network = _network;
        AppConfiguration.Instance.StreamNetwork = _streamNetwork;
        s_accountInstance.SetValue(null, _previous);
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PopulationCap_ReplacementAndDisconnectKeepTheCorrectAccount(int cap)
    {
        const uint first = 900011, second = 900012;
        var manager = CreateManager(first, second);
        WorldAdmissionStatus Request(uint account, uint token, bool permit = true) =>
            manager.PrepareAdmission(account, token, null, IPAddress.Loopback, _moderation, (ushort)cap, permit);
        var original = new GameConnection(new RecordingSession(900101));
        var replacement = new GameConnection(new RecordingSession(900102));
        var other = new GameConnection(new RecordingSession(900103));
        Assert.Equal(WorldAdmissionStatus.Reserved, Request(first, 101));
        Assert.Equal(cap == 1 ? WorldAdmissionStatus.Waiting : WorldAdmissionStatus.Reserved, Request(second, 102));
        manager.Login(original, first, 101);
        Assert.Same(original, _accounts.GetConnection(first));

        if (cap == 1)
        {
            Assert.Equal(WorldAdmissionStatus.Waiting, Request(first, 103, false));
            Assert.False(original.IsClosed);
            Assert.Same(original, _accounts.GetConnection(first));
            Assert.Equal(PendingWorldAccountResult.NotFound, manager.ConsumePendingAccount(103, first));
            Assert.Equal(WorldAdmissionStatus.Waiting, Request(second, 104));
        }
        Assert.Equal(WorldAdmissionStatus.Reserved, Request(first, 105));
        Assert.True(original.IsClosed);
        Assert.Equal(cap == 1 ? WorldAdmissionStatus.Waiting : WorldAdmissionStatus.Reserved, Request(second, 106));
        manager.Login(replacement, first, 105);
        original.OnDisconnect();
        Assert.Same(replacement, _accounts.GetConnection(first));

        if (cap == 0)
        {
            manager.Login(other, second, 106);
            Assert.Equal(2, _accounts.Count());
        }
        replacement.OnDisconnect();
        if (cap == 1)
        {
            Assert.Equal(WorldAdmissionStatus.Reserved, Request(second, 107));
            manager.Login(other, second, 107);
        }
        Assert.Same(other, _accounts.GetConnection(second));
        Assert.Equal(1, _accounts.Count());
        other.OnDisconnect();
        Assert.Equal(0, _accounts.Count());
    }

    [Fact]
    public async Task PopulationCap_AWaitingAccountCanRetryDuringAnotherAccountsLoginRewards()
    {
        const uint first = 900021, second = 900022;
        using var rewardsEntered = new ManualResetEventSlim();
        using var resumeRewards = new ManualResetEventSlim();
        _rewards.Setup(value => value.DoDailyAccountLogin(first, It.IsAny<DateOnly>())).Callback(() =>
        {
            rewardsEntered.Set();
            if (!resumeRewards.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException();
        });
        var manager = CreateManager(first, second);
        WorldAdmissionStatus Request(uint id) => manager.PrepareAdmission(id, id, null, IPAddress.Loopback, _moderation, 1, true);
        var connection = new GameConnection(new RecordingSession(900111));
        Assert.Equal(WorldAdmissionStatus.Reserved, Request(first));
        var login = Task.Run(() => manager.Login(connection, first, first));
        try
        {
            Assert.True(rewardsEntered.Wait(TimeSpan.FromSeconds(5)));
            var retry = await Task.Run(() => Request(second)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorldAdmissionStatus.Waiting, retry);
        }
        finally
        {
            resumeRewards.Set();
            await login;
        }
        Assert.Same(connection, _accounts.GetConnection(first));
        connection.OnDisconnect();
        Assert.Equal(WorldAdmissionStatus.Reserved, Request(second));
    }

    private EnterWorldManager CreateManager(params uint[] allowedAccounts)
    {
        foreach (var id in allowedAccounts)
            _moderation.ApplyState(new ModerationState(id, 1, false, 0, false, 0));
        var streams = new Mock<IStreamManager>();
        streams.Setup(stream => stream.AddToken(It.IsAny<GameConnection>())).Returns(321u);
        return new EnterWorldManager(_accounts, streams.Object, Mock.Of<IQuestManager>(), Mock.Of<IChatManager>(),
            Mock.Of<IFamilyManager>(), Mock.Of<IWorldManager>(), _moderation);
    }

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
