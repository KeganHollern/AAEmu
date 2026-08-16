using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Utils;
using AAEmu.Game.Models.Game.Units;

using NLog;

namespace AAEmu.Game.Models.Game.World;

public class AreaTrigger
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    public AreaShape Shape { get; set; }
    public Doodad Owner { get; set; }
    public BaseUnit Caster { get; set; }

    /// <summary>
    /// Units currently inside the Shape
    /// </summary>
    private List<Unit> Units { get; set; } = [];

    public uint SkillId { get; set; }
    public uint TlId { get; set; }
    public SkillTargetRelation TargetRelation { get; set; }
    public BuffTemplate InsideBuffTemplate { get; set; }
    public Dictionary<uint, List<EffectTemplate>> EffectsPerBuff { get; set; } = new();
    public int TickRate { get; set; }
    private DateTime _lastTick = DateTime.MinValue;

    /// <summary>
    /// True while at least one unit is tracked inside the shape. Lets the
    /// manager keep ticking this trigger even when the owning region has no
    /// player activity, so leave detection cannot be starved (stuck No Fight
    /// buffs after teleporting away from a quiet Nui shrine).
    /// </summary>
    public bool HasUnitsInside
    {
        get { lock (_unitsLock) return Units.Count > 0; }
    }

    private readonly object _unitsLock = new();

    private void UpdateUnits()
    {
        if (Owner == null || Shape == null || !Owner.IsVisible)
        {
            AreaTriggerManager.Instance.RemoveAreaTrigger(this);
            return;
        }

        // Get units currently in the shape. A null result means the query could
        // not run (owner region unloaded): treat it as an empty area so tracked
        // units still receive their leave events instead of being stranded with
        // the trigger's inside-buff.
        var currentUnitsInShape = WorldManager.GetAroundByShape<Unit>(Owner, Shape) ?? [];

        List<Unit> unitsSnapshot;
        lock (_unitsLock)
            unitsSnapshot = Units;
        // Check who left since last check
        var leftUnits = unitsSnapshot.Where(oldU => currentUnitsInShape.All(newU => oldU.ObjId != newU.ObjId));
        // Check who's new in the shape
        var newUnits = currentUnitsInShape.Where(newU => unitsSnapshot.All(oldU => newU.ObjId != oldU.ObjId));

        // Trigger events for new units
        foreach (var newUnit in newUnits)
        {
            OnEnter(newUnit);
            Logger.Debug($"AreaShape Enter {Shape.Id} {Shape.Type} with count {currentUnitsInShape.Count} unit in shape arround {Owner.Transform}");
        }

        // Trigger events for units that left
        foreach (var leftUnit in leftUnits)
        {
            OnLeave(leftUnit);
            Logger.Debug($"AreaShape Leave {Shape.Id} {Shape.Type} with count {currentUnitsInShape.Count} unit in shape arround {Owner.Transform}");
        }

        // Save new units list
        lock (_unitsLock)
            Units = currentUnitsInShape;
    }

    private void OnEnter(Unit unit)
    {
        if (Caster == null || Owner == null || unit == null || InsideBuffTemplate == null)
        {
            return;
        }

        if (SkillTargetingUtil.IsRelationValid(TargetRelation, Caster, unit))
        {
            unit.IncrementTriggerCount(InsideBuffTemplate.BuffId);
            if (unit.GetTriggerCount(InsideBuffTemplate.BuffId) == 1)
            {
                InsideBuffTemplate.Apply(Caster, new SkillCasterUnit(Caster.ObjId), unit, new SkillCastUnitTarget(unit.ObjId), null, new EffectSource(), null, DateTime.UtcNow);
            }
        }
    }

    private void OnLeave(Unit unit)
    {
        if (InsideBuffTemplate != null)
        {
            unit.DecrementTriggerCount(InsideBuffTemplate.BuffId);
            if (unit.GetTriggerCount(InsideBuffTemplate.BuffId) == 0)
            {
                unit.Buffs.RemoveBuff(InsideBuffTemplate.BuffId);
            }
        }
    }

    /// <summary>
    /// Immediately removes a unit from this trigger (with its inside-buff) if
    /// it is currently tracked, without waiting for the next spatial diff.
    /// Used when a unit leaves the world or teleports.
    /// </summary>
    public void ForceLeave(Unit unit)
    {
        bool wasInside;
        lock (_unitsLock)
            wasInside = Units.RemoveAll(u => u.ObjId == unit.ObjId) > 0;

        if (wasInside)
            OnLeave(unit);
    }

    public void OnDelete()
    {
        if (InsideBuffTemplate != null)
        {
            List<Unit> unitsSnapshot;
            lock (_unitsLock)
                unitsSnapshot = [.. Units];
            foreach (var unit in unitsSnapshot)
            {
                unit.DecrementTriggerCount(InsideBuffTemplate.BuffId);
                if (unit.GetTriggerCount(InsideBuffTemplate.BuffId) == 0)
                {
                    unit.Buffs.RemoveBuff(InsideBuffTemplate.BuffId);
                }
            }
        }
    }

    private void ApplyEffects()
    {
        if (InsideBuffTemplate == null || Caster == null)
        {
            return;
        }

        if (!EffectsPerBuff.ContainsKey(InsideBuffTemplate.BuffId))
        {
            return;
        }

        var unitsToApply = SkillTargetingUtil.FilterWithRelation(TargetRelation, Caster, Units);
        foreach (var unit in unitsToApply)
        {
            foreach (var effect in EffectsPerBuff[InsideBuffTemplate.BuffId])
            {
                if (effect is BuffEffect buffEffect && unit.Buffs.CheckBuff(buffEffect.BuffId))
                {
                    continue;
                }

                var eff = unit.Buffs.GetEffectFromBuffId(InsideBuffTemplate.BuffId);
                CastAction castAction = null;
                if (eff != null)
                {
                    castAction = new CastBuff(eff);
                }
                else
                {
                    castAction = new CastSkill(SkillId, 0);
                }

                effect.Apply(Caster, new SkillCasterUnit(Caster.ObjId), unit, new SkillCastUnitTarget(unit.ObjId), castAction, new EffectSource(), new SkillObject(), DateTime.UtcNow);
            }
        }
    }

    // Called every 50ms
    public void Tick(TimeSpan delta)
    {
        UpdateUnits();
        if (TickRate > 0)
        {
            if ((DateTime.UtcNow - _lastTick).TotalMilliseconds > TickRate)
            {
                ApplyEffects();
                _lastTick = DateTime.UtcNow;
            }
        }
    }
}
