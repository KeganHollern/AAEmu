using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Quests.Acts;

/// <summary>
/// Checks if a item has been obtained since the quest was started (does not require the item in the inventory)
/// </summary>
/// <param name="parentComponent"></param>
public class QuestActEtcItemObtain(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public uint ItemId { get; set; }
    public uint HighlightDoodadId { get; set; }
    public bool Cleanup { get; set; }

    /// <summary>
    /// Checks if the Objective count has been met
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    /// <returns></returns>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        var obtainedCount = quest.GetEtcItemObtainProgress(questAct.Id);
        Logger.Debug($"{QuestActTemplateName}({DetailId}).RunAct: Quest: {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), ItemId {ItemId}, Count {obtainedCount}/{Count}");
        return quest.IsEtcItemObtainComplete(questAct.Id, Count);
    }

    public override void InitializeQuest(Quest quest, QuestAct questAct)
    {
        base.InitializeQuest(quest, questAct);
        quest.Owner.Events.OnItemGather += questAct.OnItemGather;
    }

    public override void FinalizeQuest(Quest quest, QuestAct questAct)
    {
        quest.Owner.Events.OnItemGather -= questAct.OnItemGather;
        base.FinalizeQuest(quest, questAct);
    }

    public override void OnItemGather(QuestAct questAct, object sender, OnItemGatherArgs args)
    {
        var quest = questAct.QuestComponent.Parent.Parent;
        if (quest.Step == QuestComponentKind.Invalid || questAct.Id != ActId || args.ItemId != ItemId || args.Count <= 0)
            return;

        quest.AddEtcItemObtainProgress(questAct.Id, args.Count, Count);
    }

    public override void QuestCleanup(Quest quest)
    {
        base.QuestCleanup(quest);
        if (!Cleanup)
            return;

        quest.Owner?.Inventory.ConsumeItem(null, ItemTaskType.QuestRemoveSupplies, ItemId, Count, null);
    }
}
