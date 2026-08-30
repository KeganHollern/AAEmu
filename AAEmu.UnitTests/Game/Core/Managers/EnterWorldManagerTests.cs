using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class EnterWorldManagerTests
{
    private static EnterWorldManager CreateManager()
    {
        return new EnterWorldManager(
            Mock.Of<IAccountManager>().Object,
            Mock.Of<IStreamManager>().Object,
            Mock.Of<IQuestManager>().Object,
            Mock.Of<IChatManager>().Object,
            Mock.Of<IFamilyManager>().Object,
            Mock.Of<IWorldManager>().Object);
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
    [Arguments(10u)]
    [Arguments(20u)]
    public async Task SetPendingAccount_DuplicateToken_StoresLatestAccount(uint currentAccountId)
    {
        var manager = CreateManager();
        manager.SetPendingAccount(1, 10);

        manager.SetPendingAccount(1, currentAccountId);

        var result = manager.ConsumePendingAccount(1, currentAccountId);
        await Assert.That(result).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task ConsumePendingAccount_StaleClientAfterTokenReuse_PreservesCurrentAccount()
    {
        var manager = CreateManager();
        manager.SetPendingAccount(1, 10);
        manager.SetPendingAccount(1, 20);

        var staleResult = manager.ConsumePendingAccount(1, 10);
        var currentResult = manager.ConsumePendingAccount(1, 20);

        await Assert.That(staleResult).IsEqualTo(PendingWorldAccountResult.AccountMismatch);
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
