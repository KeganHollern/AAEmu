using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Models.Game.Quests;

public partial class Quest
{
    internal Quest PrepareRestart(DateTime now)
    {
        var restarted = new Quest(Template, Owner, _questManager, _taskManager, _skillManager,
            _expressTextManager, _worldManager, initializeQuestActs: false)
        {
            Id = Id,
            Status = QuestStatus.Progress,
            Condition = QuestConditionObj.Progress,
            QuestAcceptorType = QuestAcceptorType,
            AcceptorId = AcceptorId,
            _step = QuestComponentKind.Start,
            ComponentId = QuestSteps[QuestComponentKind.Start].Components.Values.First().Template.Id
        };

        // Persist a Start timer's deadline before its task or listeners become active.
        if (restarted.GetTimerActToRestore()?.Template is QuestActCheckTimer timer)
            restarted.Time = now.AddMilliseconds(timer.LimitTime);
        return restarted;
    }

    internal void ActivateRestart()
    {
        InitializeQuestActs();
        RestoreLoadedState();
        Owner.SendPacket(new SCQuestContextStartedPacket(this, ComponentId));
        QuestInitialized();
    }
}
