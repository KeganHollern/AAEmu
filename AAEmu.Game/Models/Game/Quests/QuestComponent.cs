using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Models.Game.Quests;

/// <summary>
/// Used Instance of a Quests Component
/// </summary>
public class QuestComponent : IQuestComponent
{
    public QuestComponentTemplate Template { get; set; }
    public QuestStep Parent { get; set; }
    public List<QuestAct> Acts { get; set; } = [];

    /// <summary>
    /// This is set internally to cache the enabled/disabled state for this component base of it's UnitReqs
    /// </summary>
    public bool IsCurrentlyActive { get; set; } = true;

    public QuestComponent(QuestStep parent, QuestComponentTemplate template)
    {
        Parent = parent;
        Template = template;
        foreach (var questActTemplate in Template.ActTemplates)
        {
            var newAct = new QuestAct(this, questActTemplate);
            Acts.Add(newAct);
        }
    }

    public void InitializeComponent()
    {
        foreach (var act in Acts)
            act.Template.InitializeAction(Parent.Parent, act);
    }

    public void FinalizeComponent()
    {
        foreach (var act in Acts)
            act.Template.FinalizeAction(Parent.Parent, act);
    }

    public bool RunComponent()
    {
        var conditions = Acts.Where(act => !act.Template.HasSideEffects).ToList();
        var alternatives = Parent.ThisStep is QuestComponentKind.Start or QuestComponentKind.Ready;
        var conditionsMet = alternatives && conditions.Count > 0
            ? conditions.Any(act => act.RunAct())
            : conditions.All(act => act.RunAct());
        if (!conditionsMet)
            return false;

        // Check the entire authored removal set before consuming any item (quest 2037 needs three).
        var removals = Acts.Where(act => act.Template is QuestActSupplyRemoveItem && !Parent.Parent.AppliedSideEffectActIds.Contains(act.Id)).ToList();
        if (removals.Count > 0)
        {
            var requiredItems = removals.GroupBy(act => ((QuestActSupplyRemoveItem)act.Template).ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(act => act.Template.Count));
            if (Parent.Parent.Owner.Inventory?.Bag.TryConsumeItems(ItemTaskType.QuestRemoveSupplies, requiredItems) != true)
                return false;
            foreach (var removal in removals)
                Parent.Parent.AppliedSideEffectActIds.Add(removal.Id);
        }

        foreach (var act in Acts.Where(act => act.Template.HasSideEffects && !Parent.Parent.AppliedSideEffectActIds.Contains(act.Id)))
        {
            if (!act.RunAct())
                return false;
            Parent.Parent.AppliedSideEffectActIds.Add(act.Id);
        }

        if (!Parent.Parent.AppliedComponentEffectIds.Contains(Template.Id))
        {
            Parent.Parent.UseSkillAndBuff(Template);
            Parent.Parent.SetNpcAggro(Template);
            Parent.Parent.AppliedComponentEffectIds.Add(Template.Id);
        }

        return true;
    }

    /// <summary>
    /// Sets the RequestEvaluationFlag to true signalling the server that it should check this quest's progress again
    /// </summary>
    public void RequestEvaluation()
    {
        Parent.RequestEvaluation();
    }
}
