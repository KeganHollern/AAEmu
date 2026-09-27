using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;


namespace AAEmu.Game.Models.Game.Skills.Effects;

public class EffectSource
{
    public Skill Skill { get; set; }
    internal bool IsItemProc { get => field || Skill?.IsItemProc == true; init; }
    internal byte ItemProcLevel { get => Skill?.IsItemProc == true ? Skill.Level : field; init; }
    internal AAEmu.Game.Models.Game.Duels.Duel DuelContext { get; set; }
    public BuffTemplate Buff { get; set; }
    public int Amount { get; set; }
    public bool IsTrigger { get; set; }
    internal PlotState PlotState { get; init; }
    internal float AoeDamageMultiplier { get; init; } = 1f;

    internal float GetLevelModifier(int start, int end)
    {
        // Proc ticks retain item level without acquiring an ordinary skill context.
        // Preserve the existing non-proc calculation outside this item-proc change.
        var progress = IsItemProc ? (ItemProcLevel - 1) / 49f : ((Skill?.Level ?? 1) - 1) / 49;
        return (progress * (end - start) + start) * 0.01f;
    }

    public EffectSource()
    {
    }

    public EffectSource(Skill skill)
    {
        Skill = skill;
    }

    public EffectSource(BuffTemplate buff)
    {
        Buff = buff;
    }

    public EffectSource(Skill skill, BuffTemplate buff)
    {
        Skill = skill;
        Buff = buff;
    }
}
