using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;

namespace AAEmu.Game.Models.Tasks.Quests;

public class QuestTimeoutTask : Task
{
    private readonly ICharacter _owner;
    private readonly Quest _quest;

    /// <summary>
    /// Task that triggers a OnTimerExpired event upon execution
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="quest"></param>
    public QuestTimeoutTask(ICharacter owner, Quest quest)
    {
        _owner = owner;
        _quest = quest;
    }

    public override void Execute()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!Cancelled && _owner.Quests.ActiveQuests.TryGetValue(_quest.TemplateId, out var active) &&
                ReferenceEquals(active, _quest))
                QuestManager.Instance.OnTimerExpired(_owner, _quest.TemplateId);
        }
    }
}
