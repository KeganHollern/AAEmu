using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Tasks.TimedRewards;

using Microsoft.Extensions.Options;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// For timed adding credits and loyalty
/// </summary>
public class TimedRewardsManager(
    ITaskManager taskManager,
    Lazy<IAccountManager> accountManager,
    IOptions<AppConfiguration> options) : Singleton<TimedRewardsManager>, ITimedRewardsManager
{
    public void Initialize()
    {
        taskManager.Schedule(new TimedRewardsTask(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public static short GetMaxLabor(bool isPremium)
    {
        return checked((short)PremiumGameData.Instance.Get(isPremium).MaxLabor);
    }

    /// <summary>
    /// Adds labor, internal use only
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="currentLabor"></param>
    /// <param name="addLabor"></param>
    private void DoAddLabor(GameConnection connection, short currentLabor, int addLabor)
    {
        lock (AccountManager.Instance.GetAccountSyncRoot(connection.AccountId))
        {
            var maxLaborToAdd = GetMaxLabor(connection.Payment.PremiumState) - currentLabor;
            if (maxLaborToAdd < 0)
                maxLaborToAdd = 0;
            addLabor = Math.Min(addLabor, maxLaborToAdd);
            var now = DateTime.UtcNow;
            AccountManager.Instance.UpdateTickTimes(connection.AccountId, now, true, false, false);
            if (addLabor <= 0)
                return;

            var newLabor = (short)(currentLabor + addLabor);
            AccountManager.Instance.UpdateLabor(connection.AccountId, newLabor);

            // Keep the live cache authoritative even if the notification fails.
            connection.ActiveChar?.InitializeLaborCache(newLabor, now);
            connection.ActiveChar?.SendPacket(new SCCharacterLaborPowerChangedPacket(addLabor, 0, 0, 0));
        }
    }

    public void DoTick()
    {
        var connections = GameConnectionTable.Instance.GetConnections();
        foreach (var connection in connections)
        {
            connection.ActiveChar?.RefreshPatronBuff();
            AccountDetails accountDetails;
            lock (AccountManager.Instance.GetAccountSyncRoot(connection.AccountId))
            {
                //var character = connection.ActiveChar;
                // Grab current values for last ticks
                accountDetails = AccountManager.Instance.GetAccountDetails(connection.AccountId);

                // Distribute Labor if needed (only for online labor)
                if (AppConfiguration.Instance.Labor.TickMinutes > 0 && accountDetails.LastLaborTick.AddMinutes(AppConfiguration.Instance.Labor.TickMinutes) <= DateTime.UtcNow)
                {
                    var addLabor = CalculateLabor(connection.Payment, accountDetails.LastLaborTick, DateTime.UtcNow,
                        true, AppConfiguration.Instance.Labor.TickMinutes);
                    DoAddLabor(connection, accountDetails.Labor, addLabor);
                }
            }

            // Distribute Credits if needed
            if (AppConfiguration.Instance.Credits.TickMinutes > 0 && accountDetails.LastCreditsTick.AddMinutes(AppConfiguration.Instance.Credits.TickMinutes) <= DateTime.UtcNow)
            {
                // Update Credits
                AccountManager.Instance.AddCredits(connection.AccountId, AppConfiguration.Instance.Credits.GetTickAmount(connection.Payment.PremiumState));
                AccountManager.Instance.UpdateTickTimes(connection.AccountId, DateTime.UtcNow, false, true, false);
                connection.ActiveChar?.SendPacket(new SCICSCashPointPacket(AccountManager.Instance.GetAccountDetails(connection.AccountId).Credits));
            }

            // Distribute Loyalty if needed
            if (AppConfiguration.Instance.Loyalty.TickMinutes > 0 && accountDetails.LastLoyaltyTick.AddMinutes(AppConfiguration.Instance.Loyalty.TickMinutes) <= DateTime.UtcNow)
            {
                // Update Loyalty
                AccountManager.Instance.AddLoyalty(connection.AccountId, AppConfiguration.Instance.Loyalty.GetTickAmount(connection.Payment.PremiumState));
                AccountManager.Instance.UpdateTickTimes(connection.AccountId, DateTime.UtcNow, false, false, true);
                connection.ActiveChar?.SendPacket(new SCBmPointPacket(AccountManager.Instance.GetAccountDetails(connection.AccountId).Loyalty));
            }
        }
    }

    public void DoDailyAccountLogin(uint accountId, DateOnly rewardDate)
    {
        accountManager.Value.TryClaimDailyLoginReward(
            accountId,
            rewardDate,
            options.Value.Credits.DailyLogin,
            options.Value.Loyalty.DailyLogin);
    }

    public void AddOfflineLabor(GameConnection connection, DateTime lastLaborTick, short currentLabor)
    {
        var addLabor = CalculateLabor(connection.Payment, lastLaborTick, DateTime.UtcNow,
            false, AppConfiguration.Instance.LaborOffline.TickMinutes);
        DoAddLabor(connection, currentLabor, addLabor);
    }

    internal static int CalculateLabor(AccountPayment payment, DateTime from, DateTime to, bool online, int tickMinutes)
    {
        if (tickMinutes <= 0 || to <= from)
            return 0;
        var normal = PremiumGameData.Instance.Get(false);
        var patron = PremiumGameData.Instance.Get(true);
        var baseRate = online ? normal.OnlineLabor : normal.OfflineLabor;
        var patronRate = online ? patron.OnlineLabor : patron.OfflineLabor;
        var secondsPerTick = tickMinutes * 60.0;
        var ticks = Math.Floor((to - from).TotalSeconds / secondsPerTick);
        var activeFrom = from > payment.StartTime ? from : payment.StartTime;
        var activeTo = to < payment.EndTime ? to : payment.EndTime;
        var patronTicks = activeTo <= activeFrom ? 0 : Math.Floor((activeTo - activeFrom).TotalSeconds / secondsPerTick);
        var amount = ticks * baseRate + patronTicks * (patronRate - baseRate);
        return (int)Math.Clamp(amount, 0, int.MaxValue);
    }
}
