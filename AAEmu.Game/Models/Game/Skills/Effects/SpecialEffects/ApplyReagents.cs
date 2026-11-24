using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class ApplyReagents : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.ApplyReagents;

    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int value1,
        int value2,
        int value3,
        int value4)
    {
        if (caster is Character) { Logger.Debug("Special effects: ApplyReagents value1 {0}, value2 {1}, value3 {2}, value4 {3}", value1, value2, value3, value4); }

        var skillReagents = SkillManager.Instance.GetSkillReagentsBySkillId(skill.Id);
        if (skillReagents.Count > 0)
        {
            foreach (var reagent in skillReagents)
                ConsumeReagent(reagent.ItemId, reagent.Amount);
        }
        else if (casterObj is SkillItem item)
            ConsumeReagent(item.ItemTemplateId, 1);

        void ConsumeReagent(uint templateId, int amount)
        {
            var player = (Character)caster;
            var plot = castObj is CastPlot && skill.Template.PlotOnly ? skill.ActivePlotState : null;
            lock (SaveManager.PersistenceSyncRoot)
            {
                var sourceItem = plot?.Caster == player ? ZoneSkillRestrictions.GetSourceItem(player, casterObj) : null;
                var consumed = player.Inventory.Bag.ConsumeItem(Items.Actions.ItemTaskType.SkillReagents, templateId, amount, null);
                // A plot can consume its own last reagent before it reports the item use.
                if (consumed == amount && sourceItem is { Count: 0, _holdingContainer: null } && sourceItem.TemplateId == templateId)
                    plot.ConsumedSourceItemTemplateId = templateId;
            }
        }
    }
}
