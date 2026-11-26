using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.Game.Models.Game.Skills;

public partial class Skill
{
    private Doodad _interactionDoodad;
    private enum InteractionStage { None, Starting, Casting, Firing, WaitingForEffects, Finished }
    private volatile InteractionStage _interactionStage;

    private int GetCastTime(Unit unit)
    {
        var baseTime = BaseCastingTime ?? Template.CastingTime;
        if (baseTime <= 0)
            return 0;
        var modified = (int)(unit.CastTimeMul * unit.SkillModifiersCache.ApplyModifiers(this, SkillAttribute.CastTime, baseTime));
        return (int)Math.Round(modified * CastTimeMultiplier);
    }

    private bool TryStartDoodadCast(Character caster, BaseUnit target, bool selectedByUse = false)
    {
        if (_interactionDoodad != null)
            return _interactionDoodad == target && CanFinishDoodadCast();
        if (IsItemProc || caster == null || target is not Doodad { Template: { OnceOneMan: true, HasMultipleSeats: false } } doodad ||
            (BaseCastingTime ?? Template.CastingTime) <= 0 || !Template.Effects.Any(effect => effect.Template is InteractionEffect))
            return true;

        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!selectedByUse)
            {
                var action = DoodadManager.Instance.GetFunc(doodad.FuncGroupId, Id);
                var reservesCast = action?.FuncType switch
                {
                    nameof(DoodadFuncUse) or nameof(DoodadFuncOpenPaper) or nameof(DoodadFuncRemoveItem) => true,
                    // GetFunc can return a skill-less fallback. These actions must select this skill explicitly.
                    nameof(DoodadFuncBinding) or nameof(DoodadFuncBuff) or nameof(DoodadFuncRecoverItem) => action.SkillId == Id,
                    _ => false
                };
                if (!reservesCast)
                    return true;
            }
            if (GetCastTime(caster) <= 0)
                return true;

            var casts = doodad.InteractionCasts ??= [];
            casts.RemoveWhere(skill => !skill.HasLiveDoodadCast());
            if (casts.Any(skill => skill.OriginalCaster != caster))
            {
                caster.SendErrorMessage(ErrorMessageType.AlreadyInteractingSomeoneElse);
                return false;
            }
            casts.Add(this);
            _interactionDoodad = doodad;
            _interactionStage = InteractionStage.Starting;
            return true;
        }
    }

    // The cast task clears its slot before firing. A delayed effect still owns the object
    // until it applies, even if the player starts another skill in that interval.
    private bool HasLiveDoodadCast() =>
        !Cancelled && OriginalCaster is Character { IsOnline: true, IsDead: false, IsVisible: true } owner &&
        _interactionDoodad is { IsVisible: true } doodad && owner.ParentWorld == doodad.ParentWorld &&
        (_interactionStage != InteractionStage.Casting || owner.SkillTask?.Skill == this);

    private void ReleaseDoodadCast()
    {
        if (_interactionDoodad == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        {
            _interactionDoodad?.InteractionCasts?.Remove(this);
            _interactionDoodad = null;
            _interactionStage = InteractionStage.Finished;
        }
    }

    private bool CanFinishDoodadCast() => HasLiveDoodadCast() && _interactionDoodad.InteractionCasts.Contains(this);

    internal void ReleaseSkippedDoodadCast()
    {
        if (_interactionDoodad == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_interactionStage is InteractionStage.Starting or InteractionStage.Casting)
            {
                Cancelled = true;
                ReleaseTimeline(plot: false);
            }
        }
    }

    // A zero-delay task can still wait for a scheduler tick. Acquire the child hold while
    // the parent's interaction is locked, and schedule it only after a successful commit.
    internal bool ScheduleDoodadUse(BaseUnit caster, Doodad doodad)
    {
        OriginalCaster = caster;
        if (!TryStartDoodadCast(caster as Character, doodad, selectedByUse: true))
            return false;
        var reserved = _interactionDoodad != null;
        bool Schedule()
        {
            var task = new UseSkillTask(this, caster, new SkillCasterUnit(caster.ObjId), doodad,
                new SkillCastDoodadTarget { ObjId = doodad.ObjId }, null);
            var scheduled = false;
            try
            {
                scheduled = TaskManager.Instance.Schedule(task, TimeSpan.Zero);
                return scheduled;
            }
            finally
            {
                if (!scheduled)
                    ReleaseDoodadCast();
            }
        }
        if (SkillLaborBatch.Current is { } batch)
        {
            if (reserved)
                batch.Enlist(null, ReleaseDoodadCast);
            batch.AfterCommit(() => Schedule());
            return true;
        }
        return Schedule();
    }

    internal void ApplyScheduledEffects(BaseUnit caster, SkillCaster casterCaster, BaseUnit target,
        SkillCastTarget targetCaster, SkillObject skillObject)
    {
        if (_interactionStage == InteractionStage.None)
        {
            ApplyEffects(caster, casterCaster, target, targetCaster, skillObject);
            EndSkill(caster);
            return;
        }
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_interactionStage == InteractionStage.Finished)
                return;
            try
            {
                if (!CanFinishDoodadCast())
                    Cancelled = true;
                ApplyEffects(caster, casterCaster, target, targetCaster, skillObject);
                EndSkill(caster);
            }
            finally
            {
                ReleaseDoodadCast();
            }
        }
    }
}
