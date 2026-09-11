using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Account;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class AccountManagerTests
{
    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockTick = Mock.Of<ITickManager>();
        var mockTimedRewards = Mock.Of<ITimedRewardsManager>();

        var manager = new AccountManager(mockTick.Object, mockTimedRewards.Object, TimeProvider.System);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockTick);
        Mock.VerifyNoOtherCalls(mockTimedRewards);
    }

    [Test]
    public void Initialize_AccessesOnTickProperty()
    {
        var mockTick = Mock.Of<ITickManager>();
        mockTick.OnTick.Returns(new TickManager.TickEventHandler());

        var manager = new AccountManager(
            mockTick.Object,
            Mock.Of<ITimedRewardsManager>().Object,
            TimeProvider.System);
        manager.Initialize();

        mockTick.OnTick.WasCalled(Times.Once);
    }

    [Test]
    [Arguments(-1, AccountRole.NormalPlayer)]
    [Arguments(0, AccountRole.NormalPlayer)]
    [Arguments(1, AccountRole.Moderator)]
    [Arguments(2, AccountRole.Admin)]
    [Arguments(3, AccountRole.NormalPlayer)]
    [Arguments(50, AccountRole.NormalPlayer)]
    [Arguments(100, AccountRole.NormalPlayer)]
    [Arguments(int.MaxValue, AccountRole.NormalPlayer)]
    public async Task NormalizeRole_OnlyTheThreeStoredRolesGrantAuthority(int value, AccountRole expected)
    {
        await Assert.That(AccountManager.NormalizeRole(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task AccountZero_CannotCreateAnAccountOrGrantAuthority()
    {
        var tick = Mock.Of<ITickManager>();
        var rewards = Mock.Of<ITimedRewardsManager>();
        var manager = new AccountManager(tick.Object, rewards.Object, TimeProvider.System);

        manager.Add(new GameConnection(null));

        await Assert.That(manager.Count()).IsEqualTo(0);
        await Assert.That(manager.GetAccountDetails(0).AccountId).IsEqualTo(0);
        await Assert.That(manager.GetAccountDetails(0).Role).IsEqualTo(AccountRole.NormalPlayer);
        await Assert.That(manager.GetAccountRole(0)).IsEqualTo(AccountRole.NormalPlayer);
        await Assert.That(manager.TryGetAccountRole(0, out var role)).IsFalse();
        await Assert.That(role).IsEqualTo(AccountRole.NormalPlayer);
        await Assert.That(manager.AddCredits(0, 1)).IsFalse();
        await Assert.That(manager.AddLoyalty(0, 1)).IsFalse();
        Mock.VerifyNoOtherCalls(tick);
        Mock.VerifyNoOtherCalls(rewards);
    }

    [Test]
    [Arguments(0u, 1u, AccountRole.Admin)]
    [Arguments(1u, 0u, AccountRole.Admin)]
    [Arguments(1u, 2u, (AccountRole)3)]
    [Arguments(1u, 2u, (AccountRole)255)]
    public async Task SetAccountRole_InvalidRequest_RejectsBeforeDatabaseAccess(
        uint actorAccountId, uint targetAccountId, AccountRole role)
    {
        var manager = new AccountManager(Mock.Of<ITickManager>().Object,
            Mock.Of<ITimedRewardsManager>().Object, TimeProvider.System);

        var result = manager.SetAccountRole(actorAccountId, targetAccountId, role);

        await Assert.That(result.Status).IsEqualTo(AccountRoleChangeStatus.Rejected);
        await Assert.That(result.Detail).IsNotEmpty();
    }
}
