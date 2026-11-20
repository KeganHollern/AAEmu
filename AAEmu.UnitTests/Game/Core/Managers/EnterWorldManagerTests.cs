using System.Net;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class EnterWorldManagerTests
{
    private static EnterWorldManager CreateManager(IAccountManager accounts = null, IStreamManager streams = null,
        IModerationManager moderation = null, WorldConfig startupWorld = null)
    {
        return new EnterWorldManager(
            accounts ?? Mock.Of<IAccountManager>().Object,
            streams ?? Mock.Of<IStreamManager>().Object,
            Mock.Of<IQuestManager>().Object,
            Mock.Of<IChatManager>().Object,
            Mock.Of<IFamilyManager>().Object,
            Mock.Of<IWorldManager>().Object,
            moderation,
            startupWorld);
    }

    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockAccount = Mock.Of<IAccountManager>();
        var mockStream = Mock.Of<IStreamManager>();
        var mockQuest = Mock.Of<IQuestManager>();
        var mockChat = Mock.Of<IChatManager>();
        var mockFamily = Mock.Of<IFamilyManager>();
        var mockWorld = Mock.Of<IWorldManager>();

        var manager = new EnterWorldManager(
            mockAccount.Object,
            mockStream.Object,
            mockQuest.Object,
            mockChat.Object,
            mockFamily.Object,
            mockWorld.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockAccount);
        Mock.VerifyNoOtherCalls(mockStream);
        Mock.VerifyNoOtherCalls(mockQuest);
        Mock.VerifyNoOtherCalls(mockChat);
        Mock.VerifyNoOtherCalls(mockFamily);
        Mock.VerifyNoOtherCalls(mockWorld);
    }

    [Test]
    [Arguments(WorldAccessMode.Open, AccountRole.NormalPlayer, false, true)]
    [Arguments(WorldAccessMode.AdminsOnly, AccountRole.NormalPlayer, true, false)]
    [Arguments(WorldAccessMode.AdminsOnly, AccountRole.Moderator, true, false)]
    [Arguments(WorldAccessMode.AdminsOnly, AccountRole.Admin, true, true)]
    [Arguments(WorldAccessMode.AdminsOnly, AccountRole.Admin, false, false)]
    public async Task PreparePendingAccount_AccessMode_OnlyPermitsEligibleAccount(
        WorldAccessMode mode, AccountRole role, bool roleAvailable, bool expected)
    {
        const uint accountId = 42;
        const uint token = 7;
        var accounts = Mock.Of<IAccountManager>();
        accounts.TryGetAccountRole(accountId).SetsOutRole(role).Returns(roleAvailable);
        var moderation = new ModerationManager(new PermissionManager(accounts.Object));
        moderation.ApplyState(new ModerationState(accountId, 1, false, 0, false, 0));
        var manager = CreateManager(accounts.Object, moderation: moderation, startupWorld: new WorldConfig { AccessMode = mode });

        var prepared = manager.PreparePendingAccount(accountId, token, new AccountPayment(), IPAddress.Loopback, moderation);

        await Assert.That(prepared).IsEqualTo(expected);
        await Assert.That(manager.ConsumePendingAccount(token, accountId))
            .IsEqualTo(expected ? PendingWorldAccountResult.Consumed : PendingWorldAccountResult.NotFound);
    }

    [Test, NotInParallel]
    [Arguments(AccountRole.Admin, true, true)]
    [Arguments(AccountRole.Moderator, true, false)]
    [Arguments(AccountRole.Admin, false, false)]
    public async Task Login_ValidWorldCookie_RechecksAccountRole(AccountRole role, bool roleAvailable, bool expected)
    {
        const uint accountId = 42;
        const uint token = 7;
        var accounts = Mock.Of<IAccountManager>();
        accounts.TryGetAccountRole(accountId).SetsOutRole(AccountRole.Admin).Returns(true);
        var moderation = new ModerationManager(new PermissionManager(accounts.Object));
        moderation.ApplyState(new ModerationState(accountId, 1, false, 0, false, 0));
        var streams = Mock.Of<IStreamManager>();
        var session = Mock.Of<ISession>();
        session.Ip.Returns(IPAddress.Loopback);
        var connection = new GameConnection(session.Object);
        accounts.IsCurrent(connection).Returns(true);
        streams.AddToken(connection).Returns(1u);
        var manager = CreateManager(accounts.Object, streams.Object, moderation,
            new WorldConfig { AccessMode = WorldAccessMode.AdminsOnly });
        await Assert.That(manager.PreparePendingAccount(accountId, token, new AccountPayment(), IPAddress.Loopback, moderation)).IsTrue();
        accounts.TryGetAccountRole(accountId).SetsOutRole(role).Returns(roleAvailable);

        var config = AppConfiguration.Instance;
        var network = config.Network;
        var streamNetwork = config.StreamNetwork;
        try
        {
            config.Network = new AppConfiguration.NetworkConfig();
            config.StreamNetwork = new AppConfiguration.NetworkConfig { Port = 1250 };
            manager.Login(connection, accountId, token);

            await Assert.That(connection.IsAuthenticated).IsEqualTo(expected);
            await Assert.That(connection.IsClosed).IsEqualTo(!expected);
            await Assert.That(manager.ConsumePendingAccount(token, accountId)).IsEqualTo(PendingWorldAccountResult.NotFound);
        }
        finally
        {
            config.Network = network;
            config.StreamNetwork = streamNetwork;
        }
    }

    [Test]
    [Arguments(10u)]
    [Arguments(20u)]
    public async Task SetPendingAccount_DuplicateToken_PreservesOriginalAccount(uint currentAccountId)
    {
        var manager = CreateManager();
        manager.SetPendingAccount(1, 10);

        var added = manager.SetPendingAccount(1, currentAccountId);

        await Assert.That(added).IsFalse();
        var result = manager.ConsumePendingAccount(1, 10);
        await Assert.That(result).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task ConsumePendingAccount_NewTokenForSameAccount_RejectsStaleToken()
    {
        var manager = CreateManager();
        manager.SetPendingAccount(1, 10);
        manager.SetPendingAccount(2, 10);

        var staleResult = manager.ConsumePendingAccount(1, 10);
        var currentResult = manager.ConsumePendingAccount(2, 10);

        await Assert.That(staleResult).IsEqualTo(PendingWorldAccountResult.NotFound);
        await Assert.That(currentResult).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task ConsumePendingAccount_MatchingToken_ConsumesOnlyOnce()
    {
        var manager = CreateManager();
        manager.SetPendingAccount(1, 10);

        var firstResult = manager.ConsumePendingAccount(1, 10);
        var secondResult = manager.ConsumePendingAccount(1, 10);

        await Assert.That(firstResult).IsEqualTo(PendingWorldAccountResult.Consumed);
        await Assert.That(secondResult).IsEqualTo(PendingWorldAccountResult.NotFound);
    }

    [Test]
    public async Task RemovePendingAccount_ExistingToken_RemovesToken()
    {
        var manager = CreateManager();
        manager.SetPendingAccount(1, 10);

        manager.RemovePendingAccount(1);

        var result = manager.ConsumePendingAccount(1, 10);
        await Assert.That(result).IsEqualTo(PendingWorldAccountResult.NotFound);
    }

    [Test]
    public async Task ConsumePendingAccount_ConcurrentMatchingCalls_ConsumesExactlyOnce()
    {
        var manager = CreateManager();
        manager.SetPendingAccount(1, 10);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);

        Task<PendingWorldAccountResult> ConsumeAccount()
        {
            return Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                return manager.ConsumePendingAccount(1, 10);
            });
        }

        var firstRequest = ConsumeAccount();
        var secondRequest = ConsumeAccount();
        ready.Wait();
        start.Set();

        var results = await Task.WhenAll(firstRequest, secondRequest);

        await Assert.That(results.Count(result => result == PendingWorldAccountResult.Consumed)).IsEqualTo(1);
        await Assert.That(results.Count(result => result == PendingWorldAccountResult.NotFound)).IsEqualTo(1);
    }

    [Test]
    public async Task PendingAccounts_ParallelUniqueLifecycles_AllConsumeSuccessfully()
    {
        var manager = CreateManager();
        var failures = 0;

        Parallel.For(1, 1001, value =>
        {
            var id = (uint)value;
            manager.SetPendingAccount(id, id);
            if (manager.ConsumePendingAccount(id, id) != PendingWorldAccountResult.Consumed)
                Interlocked.Increment(ref failures);
        });

        await Assert.That(failures).IsEqualTo(0);
    }
}
