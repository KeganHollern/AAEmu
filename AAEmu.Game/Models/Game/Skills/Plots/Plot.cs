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

    public async Task RunAsync(BaseUnit caster, SkillCaster casterCaster, BaseUnit target, SkillCastTarget targetCaster, SkillObject skillObject, Skill skill)
    {
        if (caster is not Unit casterUnit)
            return;

        var state = new PlotState(caster, casterCaster, target, targetCaster, skillObject, skill);
        casterUnit.ActivePlotState = state;
        skill.ActivePlotState = state;
        // I am guessing we want to do something here to run it in a thread, or at least using Async
        await Tree.ExecuteAsync(state);

        if (skill.Template.PlotOnly && !state.CancellationRequested())
            skill.RecordUseSkillAchievement(caster);

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
