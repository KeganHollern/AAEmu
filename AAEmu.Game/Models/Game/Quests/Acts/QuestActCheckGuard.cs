using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.Game.Models.Game.Quests.Acts;

public class QuestActCheckGuard(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public uint NpcId { get; set; }

    public override void InitializeAction(Quest quest, QuestAct questAct)
    {
        base.InitializeAction(quest, questAct);
        quest.ActivateGuard(NpcId);
    }

    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        return quest.CheckGuard(NpcId);
    }
}
