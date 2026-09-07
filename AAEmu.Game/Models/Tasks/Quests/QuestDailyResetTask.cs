using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.Game.Models.Tasks.Quests;

/// <summary>
/// Task that triggers daily, used for resetting daily quests, and updating daily login when the player is online
/// </summary>
public class QuestDailyResetTask : Task
{
    private readonly IWorldManager _worldManager;
    private readonly ITimedRewardsManager _timedRewardsManager;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Task used to do the quest resets for daily quests
    /// </summary>
    public QuestDailyResetTask()
    {
    }

    internal QuestDailyResetTask(
        IWorldManager worldManager,
        ITimedRewardsManager timedRewardsManager,
        TimeProvider timeProvider)
    {
        _worldManager = worldManager;
        _timedRewardsManager = timedRewardsManager;
        _timeProvider = timeProvider;
    }

    public override void Execute()
    {
        var rewardDate = DateOnly.FromDateTime(
            (_timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        foreach (var character in (_worldManager ?? WorldManager.Instance).GetAllCharacters())
        {
            character.Quests.ResetDailyQuests(true);
            (_timedRewardsManager ?? TimedRewardsManager.Instance)
                .DoDailyAccountLogin(character.AccountId, rewardDate);
        }
    }
}
