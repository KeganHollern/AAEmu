using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class TimedRewardsManagerTests
{
    [Test]
    public async Task DailyRewardManagers_ResolveWithoutCircularDependency()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<ITickManager>().Object);
        services.AddSingleton(Mock.Of<ITaskManager>().Object);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<AppConfiguration>>(Options.Create(new AppConfiguration()));
        services.AddSingleton<AccountManager>();
        services.AddSingleton<IAccountManager>(provider => provider.GetRequiredService<AccountManager>());
        services.AddSingleton<TimedRewardsManager>();
        services.AddSingleton<ITimedRewardsManager>(
            provider => provider.GetRequiredService<TimedRewardsManager>());
        services.AddSingleton(
            provider => new Lazy<IAccountManager>(provider.GetRequiredService<IAccountManager>));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        await Assert.That(provider.GetRequiredService<IAccountManager>()).IsTypeOf<AccountManager>();
        await Assert.That(provider.GetRequiredService<ITimedRewardsManager>()).IsTypeOf<TimedRewardsManager>();
    }

    [Test]
    public void DoDailyAccountLogin_ClaimsConfiguredRewardForProvidedAccountDay()
    {
        var accountManager = Mock.Of<IAccountManager>();
        var rewardDate = new DateOnly(2026, 8, 31);
        var configuration = new AppConfiguration
        {
            Credits = new CurrencyValuesConfig { DailyLogin = 3 },
            Loyalty = new CurrencyValuesConfig { DailyLogin = 5 }
        };
        var manager = new TimedRewardsManager(
            Mock.Of<ITaskManager>().Object,
            new Lazy<IAccountManager>(() => accountManager.Object),
            Options.Create(configuration));

        manager.DoDailyAccountLogin(7, rewardDate);

        accountManager.TryClaimDailyLoginReward(7, rewardDate, 3, 5).WasCalled(Times.Once);
    }
}
