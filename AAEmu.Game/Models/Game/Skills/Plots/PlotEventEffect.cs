using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Plots.Type;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Plots;

public class PlotEventEffect
{
    public int Position { get; set; }
    public PlotEffectSource SourceId { get; set; }
    public PlotEffectTarget TargetId { get; set; }
    public uint ActualId { get; set; }
    public string ActualType { get; set; }

    public void ApplyEffect(PlotState state, PlotTargetInfo targetInfo, PlotEventTemplate evt, ref byte flag, bool channeled = false, CompressedGamePackets gamePackets = null)
    {
        // Plot source/target substitutions must not change the account's original skill source.
        if (state.CancellationRequested() || state.ActiveSkill.Cancelled ||
            !ZoneSkillRestrictions.CanApply(state.Caster, state.ActiveSkill, state.CasterCaster))
        {
            state.RequestCancellation();
            return;
        }
        var template = SkillManager.Instance.GetEffectTemplate(ActualId, ActualType);

        var buffEffect = template as BuffEffect;
        if (buffEffect != null)
            flag = 2; //We still don't know what this does, but we had it as 6, and it's 2 in our packet sniffing. 

        BaseUnit source;
        switch (SourceId)
        {
            case PlotEffectSource.OriginalSource:
                source = state.Caster;
                break;
            case PlotEffectSource.OriginalTarget:
                source = state.Target;
                break;
            case PlotEffectSource.Source:
                source = targetInfo.Source;
                break;
            case PlotEffectSource.Target:
                source = targetInfo.Target;
                break;
            default:
                throw new InvalidOperationException("This can't happen");
        }

        foreach (var newTarget in targetInfo.EffectedTargets)
        {
            BaseUnit target;
            switch (TargetId)
            {
                case PlotEffectTarget.OriginalSource:
                    target = state.Caster;
                    break;
                case PlotEffectTarget.OriginalTarget:
                    target = state.Target;
                    break;
                case PlotEffectTarget.Source:
                    target = targetInfo.Source;
                    break;
                case PlotEffectTarget.Target:
                    target = newTarget;
                    break;
                case PlotEffectTarget.Location:
                    target = targetInfo.Target;
                    break;
                default:
                    throw new InvalidOperationException("This can't happen");
            }

            if (channeled && buffEffect != null)
                state.ChanneledBuffs.Add((target, buffEffect.BuffId));

            if (template == null)
            {
                return;
            }

            if (SkillLaborBatch.Current is { } batch && template is SpawnFishEffect or SpawnEffect or
                NpcSpawnerSpawnEffect or NpcSpawnerDespawnEffect or KillNpcWithoutCorpseEffect)
            {
                var deferredTarget = target;
                batch.AfterCommit(() =>
                {
                    if (!state.CancellationRequested())
                        template.Apply(source, state.CasterCaster, deferredTarget, state.TargetCaster,
                            new CastPlot(evt.PlotId, state.ActiveSkill.TlId, evt.Id, state.ActiveSkill.Template.Id),
                            new EffectSource(state.ActiveSkill), state.SkillObject, DateTime.UtcNow);
                });
                continue;
            }
            template.Apply(
                source,
                state.CasterCaster,
                target,
                state.TargetCaster,
                new CastPlot(evt.PlotId, state.ActiveSkill.TlId, evt.Id, state.ActiveSkill.Template.Id),
                new EffectSource(state.ActiveSkill),
                state.SkillObject,
                DateTime.UtcNow,
                gamePackets);

            // An effect can reject its destination after the shared source check passed.
            if (state.ActiveSkill.Cancelled || state.CancellationRequested())
            {
                state.RequestCancellation();
                return;
            }
        }
    }
}
