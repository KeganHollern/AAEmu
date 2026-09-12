using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.Game.Core.Managers;

public partial class SkillManager
{
    private HashSet<uint> _paidSkillBuffs;
    private readonly object _paidSkillBuffsLock = new();

    internal bool IsPaidSkillBuff(uint buffId)
    {
        lock (_paidSkillBuffsLock)
        {
            _paidSkillBuffs ??= CollectPaidSkillBuffs();
            return _paidSkillBuffs.Contains(buffId);
        }
    }

    private HashSet<uint> CollectPaidSkillBuffs()
    {
        var result = new HashSet<uint>();
        var visited = new HashSet<uint>();
        var pending = new Queue<SkillTemplate>(_skills.Values.Where(skill => skill.ConsumeLaborPower > 0));
        while (pending.TryDequeue(out var skill))
        {
            if (!visited.Add(skill.Id))
                continue;
            foreach (var effect in skill.Effects)
                Add(effect.Template);
            var nodes = new Queue<PlotNode>();
            var seenNodes = new HashSet<PlotNode>();
            if (skill.Plot != null)
                nodes.Enqueue(skill.Plot.Tree.RootNode);
            while (nodes.TryDequeue(out var node))
            {
                if (node == null || !seenNodes.Add(node))
                    continue;
                foreach (var effect in node.Event.Effects)
                    Add(GetEffectTemplate(effect.ActualId, effect.ActualType));
                foreach (var child in node.Children)
                    nodes.Enqueue(child);
            }
        }
        return result;

        void Add(EffectTemplate effect)
        {
            if (effect is BuffEffect buff)
                result.Add(buff.BuffId);
            else if (effect is SpecialEffect { SpecialEffectTypeId: SpecialType.SkillUse, Value1: > 0 } child &&
                _skills.TryGetValue((uint)child.Value1, out var childSkill))
                pending.Enqueue(childSkill);
        }
    }
}
