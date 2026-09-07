using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.Game.Models.Game.Quests.Acts;

public class QuestActSupplyRemoveItem(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public override bool HasSideEffects => true;

    public uint ItemId { get; set; }

    /// <summary>
    /// Removes Count amount of Item
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    /// <returns></returns>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        Logger.Debug($"{QuestActTemplateName}({DetailId}).RunAct: Quest: {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), ItemId {ItemId}, Count {Count}");

        return quest.Owner.Inventory?.Bag.TryConsumeItems(
            ItemTaskType.QuestRemoveSupplies, new Dictionary<uint, int> { [ItemId] = Count }) == true;
    }
}
