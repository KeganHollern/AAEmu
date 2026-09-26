using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class EffectSource
{
    public Skill Skill { get; set; }
    public BuffTemplate Buff { get; set; }
    public int Amount { get; set; }
    public bool IsTrigger { get; set; }
    internal PlotState PlotState { get; init; }
    internal float AoeDamageMultiplier { get; init; } = 1f;

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
