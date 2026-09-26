using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Quests.Acts;

/// <summary>
/// Counts owned items across every distinct member of a quest item group.
/// </summary>
/// <param name="parentComponent"></param>
public class QuestActObjItemGroupGather(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public override bool CountsAsAnObjective => true;
    public uint ItemGroupId { get; set; }
    public bool Cleanup { get; set; }
    public uint HighlightDoodadId { get; set; }
    public int HighlightDoodadPhase { get; set; }
    public bool UseAlias { get; set; }
    public uint QuestActObjAliasId { get; set; }
    public bool DropWhenDestroy { get; set; }
    public bool DestroyWhenDrop { get; set; }

    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        SetObjective(quest, CountOwnedItems(quest));
        return GetObjective(quest) >= Count;
    }

    public override void InitializeAction(Quest quest, QuestAct questAct)
    {
        base.InitializeAction(quest, questAct);
        SetObjective(quest, CountOwnedItems(quest));
        quest.Owner.Events.OnItemGroupGather += questAct.OnItemGroupGather;
    }

    public override void FinalizeAction(Quest quest, QuestAct questAct)
    {
        quest.Owner.Events.OnItemGroupGather -= questAct.OnItemGroupGather;
        base.FinalizeAction(quest, questAct);
    }

    public override void OnItemGroupGather(QuestAct questAct, object sender, OnItemGroupGatherArgs args)
    {
        if (questAct.Id != ActId || args.ItemGroupId != ItemGroupId ||
            !QuestManager.Instance.CheckGroupItem(ItemGroupId, args.ItemId))
            return;

        // Recount the committed inventory. Negative changes and internal moves must not add progress.
        var quest = questAct.QuestComponent.Parent.Parent;
        SetObjective(quest, CountOwnedItems(quest));
    }

    public override void QuestCleanup(Quest quest)
    {
        if (Cleanup)
            RemoveGroupItems(quest);
    }

    public override void QuestDropped(Quest quest)
    {
        if (DestroyWhenDrop)
            RemoveGroupItems(quest);
    }

    private int CountOwnedItems(Quest quest)
    {
        var count = QuestManager.Instance.GetGroupItems(ItemGroupId).Distinct()
            .Sum(itemId => (long)quest.Owner.Inventory.GetItemsCount(itemId));
        return (int)Math.Min(count, MaxObjective());
    }

    private void RemoveGroupItems(Quest quest)
    {
        // One budget covers the whole group, rather than removing Count from each member template.
        var remaining = CountOwnedItems(quest);
        foreach (var itemId in QuestManager.Instance.GetGroupItems(ItemGroupId).Distinct().Order())
        {
            if (remaining <= 0)
                break;
            remaining -= quest.Owner.Inventory.ConsumeItem(
                null, ItemTaskType.QuestRemoveSupplies, itemId, remaining, null);
        }
    }
}
