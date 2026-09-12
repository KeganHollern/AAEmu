using System.Collections.Concurrent;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Account;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Manages Connections and Game Account settings
/// </summary>
public class AccountManager(
    ITickManager tickManager,
    ITimedRewardsManager timedRewardsManager,
    TimeProvider timeProvider) : Singleton<AccountManager>, IAccountManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<uint, GameConnection> _accounts = new();
    private readonly Dictionary<uint, object> _locks = [];

    public void Initialize()
    {
        tickManager.OnTick.Subscribe(RemoveDeadConnections, TimeSpan.FromSeconds(30));
    }

    public void Add(GameConnection connection)
    {
        if (connection.AccountId == 0)
        {
            Logger.Warn("Rejected unauthenticated account registration");
            connection.Shutdown();
            return;
        }

        var loginTime = timeProvider.GetUtcNow().UtcDateTime;
        var rewardDate = DateOnly.FromDateTime(loginTime);
        if (!_accounts.TryAdd(connection.AccountId, connection))
        {
            timedRewardsManager.DoDailyAccountLogin(connection.AccountId, rewardDate);
            return;
        }

        var lastLogin = UpdateLoginTime(connection.AccountId, loginTime);
        var accountDetails = GetAccountDetails(connection.AccountId);
        timedRewardsManager.DoDailyAccountLogin(connection.AccountId, rewardDate);
        // Add offline labor
        timedRewardsManager.AddOfflineLabor(connection, lastLogin, accountDetails.Labor);
    }

    private void RemoveDeadConnections(TimeSpan delta)
    {
        foreach (var gameConnection in _accounts.Values.ToList().Where(gameConnection => gameConnection.LastPing + TimeSpan.FromSeconds(30) < DateTime.UtcNow))
        {
            if (gameConnection.ActiveChar != null)
                Logger.Trace($"Disconnecting {gameConnection.ActiveChar.Name} due to no network activity");
            gameConnection.Shutdown();
        }
    }

    public void Remove(uint id)
    {
        _accounts.TryRemove(id, out _);
    }

    public bool Contains(uint id)
    {
        return _accounts.ContainsKey(id);
    }

    public int Count() => _accounts.Count;

    internal object GetAccountSyncRoot(uint accountId)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out var accountLock))
            {
                accountLock = new object();
                _locks.Add(accountId, accountLock);
            }

            return accountLock;
        }
    }

    private AccountDetails GetAccountDetailsInternal(uint accountId)
    {
        var res = new AccountDetails();
        if (accountId == 0)
            return res;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM accounts WHERE account_id = @acc_id";
            command.Parameters.AddWithValue("@acc_id", accountId);
            command.Prepare();
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                res.AccountId = reader.GetInt32("account_id");
                res.Role = NormalizeRole(reader.GetInt32("role"));
                res.Labor = reader.GetInt16("labor");
                res.Credits = reader.GetInt32("credits");
                res.Loyalty = reader.GetInt32("loyalty");
                res.LastUpdated = reader.GetDateTime("last_updated");
                res.LastLogin = reader.GetDateTime("last_login");
                res.LastLaborTick = reader.GetDateTime("last_labor_tick");
                res.LastCreditsTick = reader.GetDateTime("last_credits_tick");
                res.LastLoyaltyTick = reader.GetDateTime("last_loyalty_tick");
                return res;
            }

            reader.Close();

            // Every new account is a normal player. Privilege comes only from an explicit role change.
            var now = timeProvider.GetUtcNow().UtcDateTime;
            command.CommandText = "INSERT INTO accounts (account_id, role, labor, credits, loyalty, last_login, last_labor_tick, last_credits_tick, last_loyalty_tick) VALUES (@acc_id, 0, @labor, @credits, @loyalty, @last_login, @last_labor_tick, @last_credits_tick, @last_loyalty_tick)";
            command.Parameters.AddWithValue("@labor", AppConfiguration.Instance.Labor.Default);
            command.Parameters.AddWithValue("@credits", AppConfiguration.Instance.Credits.Default);
            command.Parameters.AddWithValue("@loyalty", AppConfiguration.Instance.Loyalty.Default);
            command.Parameters.AddWithValue("@last_login", now);
            command.Parameters.AddWithValue("@last_labor_tick", now);
            command.Parameters.AddWithValue("@last_credits_tick", now);
            command.Parameters.AddWithValue("@last_loyalty_tick", now);
            command.Prepare();
            command.ExecuteNonQuery();
            res.AccountId = checked((int)accountId);
            res.Role = AccountRole.NormalPlayer;
            res.Labor = checked((short)AppConfiguration.Instance.Labor.Default);
            res.Credits = AppConfiguration.Instance.Credits.Default;
            res.Loyalty = AppConfiguration.Instance.Loyalty.Default;
            res.LastLogin = now;
            res.LastUpdated = now;
            res.LastLaborTick = now;
            res.LastCreditsTick = now;
            res.LastLoyaltyTick = now;
            return res;
        }
        catch (Exception e)
        {
            Logger.Error(e.Message);
            return res;
        }

    }

    public AccountDetails GetAccountDetails(uint accountId)
    {
        if (accountId == 0)
            return new AccountDetails();

        object accLock;
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out accLock))
            {
                accLock = new object();
                _locks.Add(accountId, accLock);
            }
        }
        lock (accLock)
        {
            try
            {
                return GetAccountDetailsInternal(accountId);
            }
            catch (Exception e)
            {
                Logger.Error(e.Message);
                return new AccountDetails();
            }
        }
    }

    internal static AccountRole NormalizeRole(int value)
    {
        return value is >= 0 and <= 2 ? (AccountRole)value : AccountRole.NormalPlayer;
    }

    /// <summary>
    /// Reads current authority without creating an account or using a cached character value.
    /// </summary>
    public AccountRole GetAccountRole(uint accountId)
    {
        return TryGetAccountRole(accountId, out var role) ? role : AccountRole.NormalPlayer;
    }

    /// <summary>
    /// A missing positive Game account has no staff role. The caller must separately validate a Login target.
    /// Returns false for account zero, invalid stored roles, and database failures.
    /// </summary>
    public bool TryGetAccountRole(uint accountId, out AccountRole role)
    {
        role = AccountRole.NormalPlayer;
        if (accountId == 0)
            return false;

        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT `role` FROM `accounts` WHERE `account_id` = @account_id";
            command.Parameters.AddWithValue("@account_id", accountId);
            var value = command.ExecuteScalar();
            if (value == null)
                return true;

            var storedRole = Convert.ToInt32(value);
            if (storedRole is < 0 or > 2)
            {
                Logger.Error("Account {AccountId} has invalid role {Role}", accountId, storedRole);
                return false;
            }

            role = (AccountRole)storedRole;
            return true;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to read role for account {AccountId}", accountId);
            return false;
        }
    }

    public AccountRoleChangeResult SetAccountRole(uint actorAccountId, uint targetAccountId, AccountRole role)
        => SetAccountRole(actorAccountId, targetAccountId, role, transaction => transaction.Commit());

    // The commit delegate lets tests inject a lost acknowledgement after a real database commit.
    internal AccountRoleChangeResult SetAccountRole(uint actorAccountId, uint targetAccountId, AccountRole role,
        Action<MySqlTransaction> commit)
    {
        if (actorAccountId == 0 || targetAccountId == 0 || !Enum.IsDefined(role))
        {
            return new(AccountRoleChangeStatus.Rejected,
                "A positive actor account, a positive target account, and a valid role are required.");
        }

        var commitStarted = false;
        try
        {
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT `role` FROM `accounts` WHERE `account_id` = @account_id FOR UPDATE";
            var actorRole = AccountRole.NormalPlayer;
            var targetExists = false;

            // Lock in one order across processes. Recheck the actor only after both rows are locked.
            foreach (var accountId in new[] { actorAccountId, targetAccountId }.Distinct().Order())
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@account_id", accountId);
                var value = command.ExecuteScalar();
                if (value == null)
                    continue;

                if (accountId == actorAccountId)
                    actorRole = NormalizeRole(Convert.ToInt32(value));
                if (accountId == targetAccountId)
                    targetExists = true;
            }

            if (actorRole != AccountRole.Admin)
            {
                return new(AccountRoleChangeStatus.Rejected, "Only an Admin can change account roles.");
            }

            if (!targetExists)
            {
                return new(AccountRoleChangeStatus.Rejected, "The target account does not exist in Game.");
            }

            command.Parameters.Clear();
            command.CommandText = "UPDATE `accounts` SET `role` = @role WHERE `account_id` = @account_id";
            command.Parameters.AddWithValue("@role", (byte)role);
            command.Parameters.AddWithValue("@account_id", targetAccountId);
            command.ExecuteNonQuery();
            commitStarted = true;
            commit(transaction);
            return new(AccountRoleChangeStatus.Completed, string.Empty);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to set account {TargetAccountId} role to {Role} by account {ActorAccountId}",
                targetAccountId, role, actorAccountId);
            return commitStarted
                ? new(AccountRoleChangeStatus.Unconfirmed,
                    "The account role commit was not confirmed. Check the current role before a retry.")
                : new(AccountRoleChangeStatus.Rejected, "The account role change failed before commit.");
        }
    }

    public bool AddCredits(uint accountId, int creditsAmount) => ChangeAccountCurrency(accountId, creditsAmount, "credits");

    public bool RemoveCredits(uint accountId, int credits) => credits >= 0 && ChangeAccountCurrency(accountId, -(long)credits, "credits");

    public bool AddLoyalty(uint accountId, int loyaltyAmount) => ChangeAccountCurrency(accountId, loyaltyAmount, "loyalty");

    private bool ChangeAccountCurrency(uint accountId, long delta, string column)
    {
        if (accountId == 0)
            return false;
        lock (GetAccountSyncRoot(accountId))
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.Parameters.AddWithValue("@account", accountId);
                if (delta >= 0)
                {
                    command.CommandText = "INSERT IGNORE INTO `accounts` (`account_id`) VALUES (@account)";
                    command.ExecuteNonQuery();
                }
                command.CommandText = $"UPDATE `accounts` SET `{column}` = `{column}` + @delta WHERE `account_id` = @account AND `{column}` + @delta BETWEEN 0 AND 2147483647";
                command.Parameters.AddWithValue("@delta", delta);
                return command.ExecuteNonQuery() == 1;
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Account currency change failed for {AccountId}", accountId);
                return false;
            }
        }
    }

    public bool TryClaimDailyLoginReward(
        uint accountId,
        DateOnly rewardDate,
        int creditsAmount,
        int loyaltyAmount)
    {
        creditsAmount = Math.Max(creditsAmount, 0);
        loyaltyAmount = Math.Max(loyaltyAmount, 0);

        object accLock;
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out accLock))
            {
                accLock = new object();
                _locks.Add(accountId, accLock);
            }
        }

        lock (accLock)
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                using var transaction = connection.BeginTransaction();
                var commitAttempted = false;
                try
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        "SELECT `account_id` FROM `accounts` WHERE `account_id` = @account_id FOR UPDATE";
                    command.Parameters.AddWithValue("@account_id", accountId);
                    if (command.ExecuteScalar() == null)
                        throw new InvalidOperationException($"Account {accountId} does not exist");

                    command.CommandText = """
                        INSERT INTO `account_daily_login_claims`
                            (`account_id`, `reward_date`, `credits_amount`, `loyalty_amount`, `claimed_at`)
                        VALUES
                            (@account_id, @reward_date, @credits_amount, @loyalty_amount, UTC_TIMESTAMP(3))
                        """;
                    command.Parameters.AddWithValue("@reward_date", rewardDate.ToDateTime(TimeOnly.MinValue));
                    command.Parameters.AddWithValue("@credits_amount", creditsAmount);
                    command.Parameters.AddWithValue("@loyalty_amount", loyaltyAmount);
                    command.ExecuteNonQuery();

                    command.CommandText = """
                        UPDATE `accounts`
                        SET `credits` = `credits` + @credits_amount,
                            `loyalty` = `loyalty` + @loyalty_amount,
                            `divine_clock_time` = 0,
                            `divine_clock_taken` = 0
                        WHERE `account_id` = @account_id
                        """;
                    command.ExecuteNonQuery();

                    commitAttempted = true;
                    transaction.Commit();
                    return true;
                }
                catch (MySqlException e) when (!commitAttempted && e.Number == 1062)
                {
                    RollbackDailyLoginReward(transaction, accountId, rewardDate);
                    return false;
                }
                catch (Exception e)
                {
                    RollbackDailyLoginReward(transaction, accountId, rewardDate);
                    Logger.Error(e,
                        "Failed to claim daily login reward for account {AccountId} on {RewardDate}",
                        accountId,
                        rewardDate);
                    return false;
                }
            }
            catch (Exception e)
            {
                Logger.Error(e,
                    "Failed to start daily login reward claim for account {AccountId} on {RewardDate}",
                    accountId,
                    rewardDate);
                return false;
            }
        }
    }

    private static void RollbackDailyLoginReward(
        MySqlTransaction transaction,
        uint accountId,
        DateOnly rewardDate)
    {
        try
        {
            transaction.Rollback();
        }
        catch (Exception e)
        {
            Logger.Error(e,
                "Failed to roll back daily login reward for account {AccountId} on {RewardDate}",
                accountId,
                rewardDate);
        }
    }

    public void UpdateLabor(uint accountId, short laborPower)
    {
        var accLock = GetAccountSyncRoot(accountId);
        lock (accLock)
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE accounts SET labor = @labor WHERE account_id = @account_id";
                command.Parameters.AddWithValue("@account_id", accountId);
                command.Parameters.AddWithValue("@labor", laborPower);
                command.Prepare();
                command.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Error($"{e.Message}\n{e.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Updates the login time to a new time and returns the old time
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="newTime"></param>
    /// <returns>Previous value for LastLogin</returns>
    public DateTime UpdateLoginTime(uint accountId, DateTime newTime)
    {
        object accLock;
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out accLock))
            {
                accLock = new object();
                _locks.Add(accountId, accLock);
            }
        }

        lock (accLock)
        {
            try
            {
                var res = GetAccountDetailsInternal(accountId);

                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE accounts SET last_login = @last_login WHERE account_id = @account_id";
                command.Parameters.AddWithValue("@account_id", accountId);
                command.Parameters.AddWithValue("@last_login", newTime);
                command.Prepare();
                command.ExecuteNonQuery();

                return res.LastLogin;
            }
            catch (Exception e)
            {
                Logger.Error($"{e.Message}\n{e.StackTrace}");
                return DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Updates tick timer in DB, do not set more than one flag at a time
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="newTime"></param>
    /// <param name="updateLabor"></param>
    /// <param name="updateCredits"></param>
    /// <param name="updateLoyalty"></param>
    public void UpdateTickTimes(uint accountId, DateTime newTime, bool updateLabor, bool updateCredits, bool updateLoyalty)
    {
        object accLock;
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out accLock))
            {
                accLock = new object();
                _locks.Add(accountId, accLock);
            }
        }

        lock (accLock)
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                var updateFieldName = "error";
                if (updateLabor)
                    updateFieldName = "last_labor_tick";
                if (updateCredits)
                    updateFieldName = "last_credits_tick";
                if (updateLoyalty)
                    updateFieldName = "last_loyalty_tick";
                command.CommandText = $"UPDATE accounts SET {updateFieldName} = @new_time WHERE account_id = @account_id";
                command.Parameters.AddWithValue("@account_id", accountId);
                command.Parameters.AddWithValue("@new_time", newTime);
                command.Prepare();
                command.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Error($"{e.Message}\n{e.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Adds elapsed online time without overwriting a concurrent daily clock reset.
    /// </summary>
    public void AddDivineClockTime(uint accountId, uint elapsedSeconds)
    {
        if (elapsedSeconds == 0)
            return;

        object accLock;
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out accLock))
            {
                accLock = new object();
                _locks.Add(accountId, accLock);
            }
        }

        lock (accLock)
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE `accounts`
                    SET `divine_clock_time` = `divine_clock_time` + @elapsed_seconds
                    WHERE `account_id` = @account_id
                    """;
                command.Parameters.AddWithValue("@account_id", accountId);
                command.Parameters.AddWithValue("@elapsed_seconds", elapsedSeconds);
                command.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to add divine clock time for account {AccountId}", accountId);
            }
        }
    }

    /// <summary>
    /// Updates the divine_clock_time and divine_clock_taken values for given account
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="timeElapsed"></param>
    /// <param name="timesTaken"></param>
    public void UpdateDivineClock(uint accountId, uint timeElapsed, uint timesTaken)
    {
        object accLock;
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out accLock))
            {
                accLock = new object();
                _locks.Add(accountId, accLock);
            }
        }
        lock (accLock)
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE accounts SET divine_clock_time = @divine_clock_time , divine_clock_taken = @divine_clock_taken WHERE account_id = @account_id";
                command.Parameters.AddWithValue("@account_id", accountId);
                command.Parameters.AddWithValue("@divine_clock_time", timeElapsed);
                command.Parameters.AddWithValue("@divine_clock_taken", timesTaken);
                command.Prepare();
                command.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Error($"{e.Message}\n{e.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Returns the divine_clock_time and divine_clock_taken values for given account
    /// </summary>
    /// <param name="accountId"></param>
    /// <returns></returns>
    public (uint, uint) GetDivineClock(uint accountId)
    {
        var timeElapsed = 0u;
        var timesTaken = 0u;
        object accLock;
        lock (_locks)
        {
            if (!_locks.TryGetValue(accountId, out accLock))
            {
                accLock = new object();
                _locks.Add(accountId, accLock);
            }
        }
        lock (accLock)
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT account_id, divine_clock_time, divine_clock_taken FROM accounts WHERE account_id = @account_id";
                command.Parameters.AddWithValue("@account_id", accountId);
                command.Prepare();
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    timeElapsed = reader.GetUInt32("divine_clock_time");
                    timesTaken = reader.GetUInt32("divine_clock_taken");
                }
                reader.Close();
            }
            catch (Exception e)
            {
                Logger.Error($"{e.Message}\n{e.StackTrace}");
            }
        }

        return (timeElapsed, timesTaken);
    }
}
