using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Quests;

public partial class Quest
{
    private bool _sourceLessReportAccepted;

    public HashSet<uint> AppliedSideEffectActIds { get; } = [];
    public HashSet<uint> AppliedComponentEffectIds { get; } = [];

    public bool IsValidSelectedRewardIndex(int selected)
    {
        if (!QuestSteps.TryGetValue(QuestComponentKind.Reward, out var rewardStep))
            return true;

        var hasSelectiveRewards = false;
        foreach (var component in rewardStep.Components.Values)
        {
            var selectiveActs = component.Acts.Where(act => act.Template is QuestActSupplySelectiveItem).ToList();
            hasSelectiveRewards |= selectiveActs.Count > 0;
            if (UnitRequirementsGameData.Instance.CanComponentRun(component.Template, (BaseUnit)Owner) &&
                selectiveActs.Any(act => act.Template.ThisSelectiveIndex == selected))
                return true;
        }

        // The field is unused for quests without selective rewards.
        return !hasSelectiveRewards;
    }

    internal bool TryReportWithoutSource(int selected)
    {
        if (_sourceLessReportAccepted ||
            (Step != QuestComponentKind.Ready && !(Step == QuestComponentKind.Progress && Template.LetItDone)) ||
            !Owner.Quests.ActiveQuests.TryGetValue(TemplateId, out var activeQuest) || !ReferenceEquals(activeQuest, this) ||
            !IsValidSelectedRewardIndex(selected))
            return false;

        var hasReadyStep = QuestSteps.TryGetValue(QuestComponentKind.Ready, out var reportStep);
        if (!hasReadyStep && !QuestSteps.TryGetValue(QuestComponentKind.Reward, out reportStep))
            return false;
        if (reportStep.Components.Count == 0)
            return false;

        // Ready components explicitly author journal/automatic routes. Some LetItDone quests omit Ready
        // and put ConAutoComplete in Reward instead (for example, r208022 quests 3419 and 4967).
        foreach (var component in reportStep.Components.Values)
        {
            if (!UnitRequirementsGameData.Instance.CanComponentRun(component.Template, (BaseUnit)Owner) ||
                !component.Acts.Any(act => act.Template is QuestActConAutoComplete ||
                    (hasReadyStep && act.Template is QuestActConReportJournal)))
                return false;
        }

        if (QuestSteps.TryGetValue(QuestComponentKind.Progress, out var progressStep))
        {
            foreach (var component in progressStep.Components.Values)
            {
                component.IsCurrentlyActive = UnitRequirementsGameData.Instance.CanComponentRun(component.Template, (BaseUnit)Owner);
                // These authored Progress routes contain objectives only; do not skip a pending supply operation.
                if (Step == QuestComponentKind.Progress && component.Acts.Any(act => act.Template.HasSideEffects))
                    return false;
            }
        }
        if (GetQuestObjectiveStatus() < QuestObjectiveStatus.QuestComplete)
            return false;

        _sourceLessReportAccepted = true;
        SelectedRewardIndex = selected;
        foreach (var act in reportStep.Components.Values.SelectMany(component => component.Acts))
        {
            if (act.Template is QuestActConReportJournal)
                act.OverrideObjectiveCompleted = true;
        }
        Step = hasReadyStep ? QuestComponentKind.Ready : QuestComponentKind.Reward;
        RequestEvaluation();
        return true;
    }
}
