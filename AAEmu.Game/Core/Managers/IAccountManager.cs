using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Account;

namespace AAEmu.Game.Core.Managers;

public interface IAccountManager : IInitializable
{
    void Add(GameConnection connection);
    void Remove(uint id);
    void Remove(GameConnection connection);
    GameConnection GetConnection(uint accountId);
    bool IsCurrent(GameConnection connection);
    bool Contains(uint id);
    int Count();
    AccountDetails GetAccountDetails(uint accountId);
    AccountRole GetAccountRole(uint accountId);
    bool TryGetAccountRole(uint accountId, out AccountRole role);
    AccountRoleChangeResult SetAccountRole(uint actorAccountId, uint targetAccountId, AccountRole role);
    bool AddCredits(uint accountId, int creditsAmount);
    bool RemoveCredits(uint accountId, int credits);
    bool AddLoyalty(uint accountId, int loyaltyAmount);
    bool TryClaimDailyLoginReward(uint accountId, DateOnly rewardDate, int creditsAmount, int loyaltyAmount);
    void UpdateLabor(uint accountId, short laborPower);
    DateTime UpdateLoginTime(uint accountId, DateTime newTime);
    void UpdateTickTimes(uint accountId, DateTime newTime, bool updateLabor, bool updateCredits, bool updateLoyalty);
    void AddDivineClockTime(uint accountId, uint elapsedSeconds);
    void UpdateDivineClock(uint accountId, uint timeElapsed, uint timesTaken);
    (uint, uint) GetDivineClock(uint accountId);
}
