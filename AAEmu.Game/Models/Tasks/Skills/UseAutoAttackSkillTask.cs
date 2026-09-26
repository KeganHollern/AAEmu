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
    private int _mainhandAttackIndex;
    private int _offhandAttackIndex;

    /// <summary>
    /// Cached offhand attack options. Each shot gets a separate Skill instance.
    /// Current equipment decides whether the offhand fires on this tick.
    /// </summary>
    private Skill _offhandSkill;
    private SkillTemplate _offhandSkillTemplate;

    public UseAutoAttackSkillTask(Skill skill, Character caster) : base(skill)
    {
        _mainhandSkill = skill;
        _mainhandAttackIndex = skill.AutoAttackIndex;
        _caster = caster;
        Cancelled = false;
    }

    public override void Execute()
    {
        if (!ReferenceEquals(_caster.AutoAttackTask, this))
        {
            StopAutoAttack();
            return;
        }

        var target = _caster.CurrentTarget as Unit;

        // Stop conditions: dead, no target, target dead, self-target, cancelled
        if (_caster.Hp <= 0 || target == null || target.Hp <= 0 || target.ObjId == _caster.ObjId || Cancelled)
        {
            StopAutoAttack();
            return;
        }

        RefreshAttackInterval();

        // Skill-pause: while another skill is casting or during GCD, skip this tick.
        // We don't cancel — auto-attack will resume on the next tick after skill ends.
        if (_caster.SkillTask != null)
            return;
        if (_caster.GlobalCooldown >= DateTime.UtcNow)
            return;

        if (!_caster.CanAttack(target))
            return;

        // Range check — pause (don't cancel) if target moved out of range
        var maxRange = GetEffectiveMaxRange();
        var distance = _caster.GetDistanceTo(target);
        if (distance > maxRange)
            return;

        var casterCaster = new SkillCasterUnit(_caster.ObjId);
        var targetCaster = new SkillCastUnitTarget(target.ObjId);
        var skillObject = SkillObject.GetByType(SkillObjectType.None);

        // Delayed impacts retain their own timeline, target, and cancellation state.
        var mainhand = CreateAttack(_mainhandSkill, _mainhandAttackIndex);
        mainhand.Use(_caster, casterCaster, targetCaster, skillObject, true, out _);
        _mainhandAttackIndex = mainhand.AutoAttackIndex;

        // Dual-wield: check the current equipment on every tick (not just at task
        // construction). If the player swaps weapons mid-fight, the offhand swing
        // appears / disappears immediately on the next tick.
        var offhandSkill = ResolveOffhandSkill();
        if (offhandSkill != null)
        {
            var offCaster = new SkillCasterUnit(_caster.ObjId);
            var offTarget = new SkillCastUnitTarget(target.ObjId);
            var offSkillObject = SkillObject.GetByType(SkillObjectType.None);
            var offhand = CreateAttack(offhandSkill, _offhandAttackIndex);
            offhand.Use(_caster, offCaster, offTarget, offSkillObject, true, out _);
            _offhandAttackIndex = offhand.AutoAttackIndex;
        }

        // A hit can add or remove a speed buff. Update the pending interval too.
        RefreshAttackInterval();
    }

    private static Skill CreateAttack(Skill options, int animationIndex) => new(options.Template)
    {
        Id = options.Id,
        Level = options.Level,
        BaseCastingTime = options.BaseCastingTime,
        CastTimeMultiplier = options.CastTimeMultiplier,
        Callback = options.Callback,
        AutoAttackIndex = animationIndex
    };

    /// <summary>
    /// Decide per tick whether an offhand auto-attack should fire alongside the
    /// mainhand. Returns the offhand Skill if and only if:
    ///   • mainhand task is the melee skill (id 2)
    ///   • offhand currently holds a weapon (anything that's not a shield / empty)
    /// The Skill instance itself is cached lazily on first hit.
    /// </summary>
    private Skill ResolveOffhandSkill()
    {
        if (_mainhandSkill.Template.Id != 2)
            return null;

        var offhandItem = _caster.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Offhand);
        if (offhandItem?.Template is not WeaponTemplate offhandWeapon)
            return null;

        // A shield is also a WeaponTemplate, but it must NOT produce an offhand
        // auto-attack swing. Without this guard a sword+shield loadout swung the
        // shield as if it were a dual-wield weapon (#1454).
        if (offhandWeapon.HoldableTemplate?.SlotTypeId == (uint)EquipmentItemSlotType.Shield)
            return null;

        if (_offhandSkill != null)
            return _offhandSkill;

        _offhandSkillTemplate ??= SkillManager.Instance.GetSkillTemplate(3);
        if (_offhandSkillTemplate == null)
            return null;

        _offhandSkill = new Skill(_offhandSkillTemplate);
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

    private void RefreshAttackInterval()
    {
        var interval = TimeSpan.FromMilliseconds(SkillManager.GetAttackDelay(_mainhandSkill.Template, _caster));
        TaskManager.Instance.UpdateRepeatInterval(this, interval);
    }

    /// <summary>Get max attack range from equipped weapon or fall back to skill template.</summary>
    private float GetEffectiveMaxRange()
    {
        var slot = _mainhandSkill.Template.Id == 4
            ? EquipmentItemSlot.Ranged
            : EquipmentItemSlot.Mainhand;

        var weapon = _caster.Equipment?.GetItemBySlot((int)slot);
        if (weapon?.Template is WeaponTemplate wt && wt.HoldableTemplate != null && wt.HoldableTemplate.MaxRange > 0)
            return wt.HoldableTemplate.MaxRange;

        return _mainhandSkill.Template.MaxRange > 0 ? _mainhandSkill.Template.MaxRange : 3f;
    }
}
