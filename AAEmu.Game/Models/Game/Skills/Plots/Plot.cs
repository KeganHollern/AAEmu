using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Plots;

public class Plot
{
    public uint Id { get; set; }
    public uint TargetTypeId { get; set; }

    // Probably not needed anymore
    public PlotEventTemplate EventTemplate { get; set; }

    public PlotTree Tree { get; set; }

    public Task RunAsync(BaseUnit caster, SkillCaster casterCaster, BaseUnit target, SkillCastTarget targetCaster, SkillObject skillObject, Skill skill)
    {
        var state = PrepareRun(caster, casterCaster, target, targetCaster, skillObject, skill);
        return state == null ? Task.CompletedTask : RunAsync(state);
    }

    internal static PlotState PrepareRun(BaseUnit caster, SkillCaster casterCaster, BaseUnit target, SkillCastTarget targetCaster, SkillObject skillObject, Skill skill)
    {
        if (caster is not Unit casterUnit)
            return null;

        var state = new PlotState(caster, casterCaster, target, targetCaster, skillObject, skill);
        state.SetPendingExecution();
        if (!skill.IsItemProc)
            casterUnit.ActivePlotState = state;
        skill.ActivePlotState = state;
        return state;
    }

    internal async Task RunAsync(PlotState state)
    {
        var skill = state.ActiveSkill;
        var caster = state.Caster;
        var casterCaster = state.CasterCaster;
        await Tree.ExecuteAsync(state);

        if (skill.Template.PlotOnly && !state.CancellationRequested())
        {
            if (caster is Character laborOwner && skill.Template.ConsumeLaborPower > 0 && !skill.LaborSettled &&
                !SkillLaborBatch.Run(laborOwner, skill, true, () => { }))
                state.RequestCancellation();
            if (!state.CancellationRequested())
                skill.RecordUseSkillAchievement(caster);
        }

        if (casterCaster is SkillItem skillItem && caster is Character player)
        {
            lock (SaveManager.PersistenceSyncRoot)
            {
                var item = player.Inventory.GetItemById(skillItem.ItemId);
                if (item == null)
                    return;
                if (!state.CancellationRequested())
                    player.ItemUse(item);
                player.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.ItemUnlock, new ItemUpdate(item), []));
            }
        }
    }
}
