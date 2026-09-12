using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Static;

namespace AAEmu.Game.Models.Game.Skills;

public partial class Skill
{
    internal bool LaborSettled { get; set; }
    internal bool LaborVocationSettled { get; set; }
    private bool _normalLaborEffectsCompleted;
    private SkillCaster _laborCaster;
    private SkillCastTarget _laborTarget;
    private SkillObject _laborObject;
    internal Func<Character, Action<PersistenceSaveContext>, bool> CommitLaborBatch { get; set; } =
        (character, writeLabor) => SaveManager.Instance.TryCommitEconomy(SkillLaborBatch.Current?.Participants ?? [character], writeLabor);

    internal int GetLaborCost(Character character)
    {
        var cost = Template.ConsumeLaborPower;
        if (cost <= 0)
            return 0;
        var ability = character.Actability?.Actabilities.GetValueOrDefault((uint)Template.ActabilityGroupId);
        return Math.Max(1, (int)Math.Round(cost * (ability?.GetLaborCostMultiplier() ?? 1f)));
    }

    internal bool RejectLabor(Character character)
    {
        Cancelled = true;
        character.SkillCancelled = true;
        character.Craft?.CancelFromSkill(Template.Id);
        if (_laborCaster != null && _laborTarget != null)
            character.SendPacket(new SCSkillStartedPacket(Id, 0, _laborCaster, _laborTarget, this, _laborObject)
                .SetSkillResult(SkillResult.NeedLaborPower));
        else
            character.SendErrorMessage(ErrorMessageType.NotEnoughLaborPower);
        return false;
    }

    internal bool RunPlotLaborBatch(PlotNode node, PlotState state, Action effects)
    {
        if (state.Caster is not Character character || Template.ConsumeLaborPower <= 0)
        {
            effects();
            return !Cancelled;
        }
        _laborCaster = state.CasterCaster;
        _laborTarget = state.TargetCaster;
        _laborObject = state.SkillObject;
        var charge = node.Event.Effects.Any(effect =>
        {
            var template = SkillManager.Instance.GetEffectTemplate(effect.ActualId, effect.ActualType);
            return template is GainLootPackItemEffect or CraftEffect or InteractionEffect or SpawnFishEffect or BuffEffect or DispelEffect or
                SpawnEffect or NpcSpawnerSpawnEffect or NpcSpawnerDespawnEffect or KillNpcWithoutCorpseEffect ||
                template is SpecialEffect { SpecialEffectTypeId: SpecialType.ConsumeLaborPower or SpecialType.FishingLoot or SpecialType.GainItem or SpecialType.GiveLivingPoint or SpecialType.AddExp or SpecialType.SpawnDoodad };
        });
        // Plots without an explicit labor marker still pay at the first node after casting.
        if (!charge && !PlotHasLaborMarker() && (node.ParentNextEvent is { Casting: true } ||
            node.Event.Effects.Any(effect => SkillManager.Instance.GetEffectTemplate(effect.ActualId, effect.ActualType) is BuffEffect)))
            charge = true;
        var succeeded = SkillLaborBatch.Run(character, this, charge, effects);
        if (!succeeded)
            state.RequestCancellation();
        return succeeded;
    }

    internal bool CanSettleLaborEffects(BaseUnit caster, BaseUnit target)
    {
        if (caster is not Character || Template.ConsumeLaborPower <= 0)
            return true;
        var effects = Template.Effects.Select(effect => effect.Template).ToList();
        var seen = new HashSet<PlotNode>();
        var queue = new Queue<PlotNode>();
        if (Template.Plot != null)
            queue.Enqueue(Template.Plot.Tree.RootNode);
        while (queue.TryDequeue(out var node))
        {
            if (node == null || !seen.Add(node))
                continue;
            effects.AddRange(node.Event.Effects.Select(effect => SkillManager.Instance.GetEffectTemplate(effect.ActualId, effect.ActualType)));
            foreach (var child in node.Children)
                queue.Enqueue(child);
        }
        if (effects.All(CanSettleLaborEffect))
            return true;
        Cancelled = true;
        (caster as Character)?.SendErrorMessage(ErrorMessageType.InternalError);
        return false;
    }

    internal static bool CanSettleLaborEffect(EffectTemplate effect) => effect switch
    {
        null => true,
        GainLootPackItemEffect or InteractionEffect or CraftEffect or BuffEffect or DispelEffect or BubbleEffect or SpawnFishEffect or
            SpawnEffect or NpcSpawnerSpawnEffect or NpcSpawnerDespawnEffect or KillNpcWithoutCorpseEffect => true,
        SpecialEffect special => special.SpecialEffectTypeId is SpecialType.GainItem or SpecialType.ApplyReagents or
            SpecialType.ConsumeLaborPower or SpecialType.GiveLivingPoint or SpecialType.FishingLoot or
            SpecialType.GradeEnchant or SpecialType.ItemConversion or SpecialType.AddExp or SpecialType.SpawnDoodad or
            SpecialType.Anim or SpecialType.FxGroup or SpecialType.FxGroupAnim or SpecialType.Projectile or
            SpecialType.ProjectileAnim or SpecialType.ManaCost or SpecialType.Cooldown or SpecialType.GlobalCooldown or
            SpecialType.CancelStealth or SpecialType.CancelOngoingBuff or SpecialType.CombatDice or
            SpecialType.ClearProjectile or SpecialType.RetrieveProjectile or SpecialType.AddFxToProjectile or
            SpecialType.StopChanneling or SpecialType.FinishChanneling or SpecialType.SetVariable,
        _ => false
    };

    private bool PlotHasLaborMarker()
    {
        var seen = new HashSet<PlotNode>();
        var queue = new Queue<PlotNode>();
        queue.Enqueue(Template.Plot.Tree.RootNode);
        while (queue.TryDequeue(out var node))
        {
            if (node == null || !seen.Add(node))
                continue;
            if (node.Event.Effects.Any(effect => SkillManager.Instance.GetEffectTemplate(effect.ActualId, effect.ActualType)
                is SpecialEffect { SpecialEffectTypeId: SpecialType.ConsumeLaborPower }))
                return true;
            foreach (var child in node.Children)
                queue.Enqueue(child);
        }
        return false;
    }
}
