using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Quests.Acts;

public class QuestActCheckSphere(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public uint SphereId { get; set; }

    public override void InitializeAction(Quest quest, QuestAct questAct)
    {
        base.InitializeAction(quest, questAct);
        ((GameObject)quest.Owner).ParentWorld.SphereQuestManager.AddSphereQuestTriggers(quest.Owner, quest, ParentComponent.Id, 0, SphereId);
        quest.Owner.Events.OnEnterSphere += questAct.OnEnterSphere;
        quest.Owner.Events.OnExitSphere += questAct.OnExitSphere;
    }

    public override void FinalizeAction(Quest quest, QuestAct questAct)
    {
        ((GameObject)quest.Owner).ParentWorld.SphereQuestManager.RemoveSphereQuestTriggers(quest.Owner.Id, quest.TemplateId);
        quest.Owner.Events.OnEnterSphere -= questAct.OnEnterSphere;
        quest.Owner.Events.OnExitSphere -= questAct.OnExitSphere;
        base.FinalizeAction(quest, questAct);
    }

    /// <summary>
    /// Checks if you are inside a specific Quest Sphere
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    /// <returns></returns>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        Logger.Debug($"{QuestActTemplateName}({DetailId}).RunAct: Quest {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), SphereId {SphereId}");
        return questAct.OverrideObjectiveCompleted;
    }

    public override void OnEnterSphere(QuestAct questAct, object sender, OnEnterSphereArgs args)
    {
        if (questAct.Id != ActId || args.SphereQuest.QuestId != ParentQuestTemplate.Id ||
            args.SphereQuest.ComponentId != ParentComponent.Id)
            return;
        Logger.Debug($"{QuestActTemplateName}({DetailId}).OnEnterSphere: Quest {questAct.QuestComponent.Parent.Parent.TemplateId}, Owner {questAct.QuestComponent.Parent.Parent.Owner.Name} ({questAct.QuestComponent.Parent.Parent.Owner.Id}), SphereId {SphereId}");
        questAct.OverrideObjectiveCompleted = true;
    }

    public override void OnExitSphere(QuestAct questAct, object sender, OnExitSphereArgs args)
    {
        if (questAct.Id != ActId || args.SphereQuest.QuestId != ParentQuestTemplate.Id ||
            args.SphereQuest.ComponentId != ParentComponent.Id)
            return;
        Logger.Debug($"{QuestActTemplateName}({DetailId}).OnExitSphere: Quest {questAct.QuestComponent.Parent.Parent.TemplateId}, Owner {questAct.QuestComponent.Parent.Parent.Owner.Name} ({questAct.QuestComponent.Parent.Parent.Owner.Id}), SphereId {SphereId}");
        questAct.OverrideObjectiveCompleted = false;
    }
}
