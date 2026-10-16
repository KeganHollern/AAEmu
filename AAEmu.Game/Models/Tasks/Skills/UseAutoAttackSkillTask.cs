using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Tasks.Skills;

public class UseAutoAttackSkillTask : SkillTask
{
    private readonly Character _caster;
    private readonly Skill _mainhandSkill;
    private readonly Func<DateTime> _utcNow;
    private readonly AutoAttackCycle _mainhandCycle = new();
    private readonly AutoAttackCycle _offhandCycle = new();
    private int _executing;
    private int _mainhandAttackIndex;
    private int _offhandAttackIndex;
    private Skill _offhandSkill;

    public UseAutoAttackSkillTask(Skill skill, Character caster) : this(skill, caster, () => DateTime.UtcNow)
    {
    }

    internal UseAutoAttackSkillTask(Skill skill, Character caster, Func<DateTime> utcNow) : base(skill)
    {
        _mainhandSkill = skill;
        _mainhandAttackIndex = skill.AutoAttackIndex;
        _caster = caster;
        _utcNow = utcNow;
        RefreshCycles(_utcNow());
    }

    public bool Schedule()
    {
        var now = _utcNow();
        RefreshCycles(now);
        return TaskManager.Instance.Schedule(this, NextTrigger - now, FallbackInterval);
    }

    public override void Execute()
    {
        // A slow callback must not overlap a second callback for this same task.
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            return;
        try
        {
            ExecuteAttack();
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
        }
    }

    private void ExecuteAttack()
    {
        if (!ReferenceEquals(_caster.AutoAttackTask, this))
        {
            StopAutoAttack();
            return;
        }

        var target = _caster.CurrentTarget as Unit;
        if (_caster.Hp <= 0 || target == null || target.Hp <= 0 || target.ObjId == _caster.ObjId || Cancelled)
        {
            StopAutoAttack();
            return;
        }

        var now = _utcNow();
        RefreshCycles(now);
        var mainhandDue = _mainhandCycle.TryConsume(now);

        // Pauses consume each due attempt, as the former recurring task did.
        // Each hand keeps its own cycle and resumes without accumulated swings.
        if (mainhandDue && CanAttack(target, _mainhandSkill, now))
        {
            var mainhand = CreateAttack(_mainhandSkill, _mainhandAttackIndex);
            UseAttack(mainhand, target);
            _mainhandAttackIndex = mainhand.AutoAttackIndex;
        }

        // The mainhand hit can change equipment, speed, or the target's life.
        // Read those values again before a separately due offhand attempt.
        RefreshCycles(now);
        if (_offhandCycle.TryConsume(now) && CanAttack(target, _offhandSkill, now))
        {
            var offhand = CreateAttack(_offhandSkill, _offhandAttackIndex);
            UseAttack(offhand, target);
            _offhandAttackIndex = offhand.AutoAttackIndex;
        }

        if (Cancelled || !ReferenceEquals(_caster.AutoAttackTask, this) || _caster.Hp <= 0 || target.Hp <= 0)
        {
            StopAutoAttack();
            return;
        }

        RefreshCycles(now);
        TaskManager.Instance.SetNextTrigger(this, NextTrigger, FallbackInterval);
    }

    private bool CanAttack(Unit target, Skill skill, DateTime now) =>
        skill != null && !Cancelled && ReferenceEquals(_caster.AutoAttackTask, this) &&
        _caster.Hp > 0 && target.Hp > 0 &&
        _caster.SkillTask == null && _caster.ActivePlotState?.HasCastWaits != true &&
        _caster.GlobalCooldown < now && _caster.CanAttack(target) &&
        _caster.GetDistanceTo(target) <= GetEffectiveMaxRange(skill.Template);

    private void UseAttack(Skill attack, Unit target) =>
        attack.Use(_caster, new SkillCasterUnit(_caster.ObjId), new SkillCastUnitTarget(target.ObjId),
            SkillObject.GetByType(SkillObjectType.None), true, out _);

    private static Skill CreateAttack(Skill options, int animationIndex) => new(options.Template)
    {
        Id = options.Id,
        Level = options.Level,
        BaseCastingTime = options.BaseCastingTime,
        CastTimeMultiplier = options.CastTimeMultiplier,
        Callback = options.Callback,
        AutoAttackIndex = animationIndex
    };

    private Skill ResolveOffhandSkill()
    {
        if (_mainhandSkill.Template.Id != 2)
            return null;

        var offhandItem = _caster.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Offhand);
        if (offhandItem?.Template is not WeaponTemplate { HoldableTemplate: { } holdable } ||
            holdable.SlotTypeId == (uint)EquipmentItemSlotType.Shield)
            return null;

        if (_offhandSkill != null)
            return _offhandSkill;

        var template = SkillManager.Instance.GetSkillTemplate(3);
        if (template != null)
            _offhandSkill = new Skill(template);
        return _offhandSkill;
    }

    private void StopAutoAttack()
    {
        Cancelled = true;
        if (ReferenceEquals(_caster.AutoAttackTask, this))
        {
            _caster.IsAutoAttack = false;
            _caster.AutoAttackTask = null;
        }
        Cancel();
    }

    private void RefreshCycles(DateTime now)
    {
        _mainhandCycle.Refresh(now, SkillManager.GetAttackDelay(_mainhandSkill.Template, _caster));
        var offhand = ResolveOffhandSkill();
        _offhandCycle.Refresh(now, offhand == null ? 1 : SkillManager.GetAttackDelay(offhand.Template, _caster),
            offhand != null);
    }

    private DateTime NextTrigger => _offhandCycle.Enabled && _offhandCycle.DueTime < _mainhandCycle.DueTime
        ? _offhandCycle.DueTime : _mainhandCycle.DueTime;

    private TimeSpan FallbackInterval => _offhandCycle.Enabled && _offhandCycle.Interval < _mainhandCycle.Interval
        ? _offhandCycle.Interval : _mainhandCycle.Interval;

    private float GetEffectiveMaxRange(SkillTemplate template)
    {
        var slot = template.Id switch
        {
            3 => EquipmentItemSlot.Offhand,
            4 => EquipmentItemSlot.Ranged,
            _ => EquipmentItemSlot.Mainhand
        };
        var weapon = _caster.Equipment?.GetItemBySlot((int)slot);
        if (weapon?.Template is WeaponTemplate wt && wt.HoldableTemplate != null && wt.HoldableTemplate.MaxRange > 0)
            return wt.HoldableTemplate.MaxRange;
        return template.MaxRange > 0 ? template.MaxRange : 3f;
    }
}
