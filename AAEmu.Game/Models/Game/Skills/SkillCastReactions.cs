using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.Game.Models.Game.Skills;

internal static class SkillCastReactions
{
    internal static void OnMovement(Unit unit, Vector3 oldPosition, Vector3 newPosition,
        bool sameParent, bool turned)
    {
        // Compare accepted local coordinates. A carried actor does not move locally.
        // Active server controllers own forced movement and must finish their skill.
        if (unit.ActiveSkillController != null)
            return;
        var encoded = Helpers.ConvertPosition(oldPosition.X, oldPosition.Y, oldPosition.Z);
        var (x, y, z) = Helpers.ConvertPosition(encoded);
        var moved = !sameParent || (oldPosition != newPosition && new Vector3(x, y, z) != newPosition);
        if (!moved && !turned)
            return;

        var plot = unit.ActivePlotState;
        if (plot != null && (moved || plot.ActiveSkill.Template.StopCastingByTurn))
            plot.CancelCastWaits();

        var task = unit.SkillTask;
        if (task?.CastWindow == null || (!moved && !task.Skill.Template.StopCastingByTurn) ||
            !task.CastWindow.TryCancel())
            return;
        task.Skill.Stop(unit, (task as EndChannelingTask)?._channelDoodad);
    }

    internal static void OnDamage(Unit unit, int damage, DateTime now)
    {
        if (damage <= 0 || unit.Hp <= 0 || unit.MaxHp <= 0)
            return;
        var task = unit.SkillTask;
        var plot = unit.ActivePlotState;
        if (task?.CastWindow is not { Delayable: true, Active: true } &&
            (plot == null || !plot.HasDelayableCastWaits))
            return;

        var delay = CalculateDamageDelay(unit, damage);
        if (delay <= 0)
            return;
        ushort skillTl = 0;
        ushort plotTl = 0;
        if (task?.CastWindow != null && ReferenceEquals(unit.SkillTask, task) && !task.Skill.Cancelled &&
            task.CastWindow.TryDelay(now, delay))
            skillTl = task.Skill.TlId;
        if (plot != null && ReferenceEquals(unit.ActivePlotState, plot) && !plot.CancellationRequested() && plot.DelayCastWaits(now, delay))
            plotTl = plot.ActiveSkill.TlId;
        if (skillTl != 0 || plotTl != 0)
            unit.BroadcastPacket(new SCCastingDelayedPacket(skillTl, plotTl, (uint)delay), true);
    }

    internal static int CalculateDamageDelay(Unit unit, int damage)
    {
        var manager = FormulaManager.Instance;
        var toleranceFormula = unit.BaseUnitType != BaseUnitType.Invalid
            ? manager.GetUnitFormula((FormulaOwnerType)unit.BaseUnitType, UnitFormulaKind.CastingTolerance)
            : null;
        var tolerance = toleranceFormula?.Evaluate(new Dictionary<string, double> { ["level"] = unit.Level }) ?? 100d;
        tolerance = unit.CalculateWithBonuses(tolerance, UnitAttribute.CastingTolerance);
        if (!double.IsFinite(tolerance) || tolerance <= 0)
            return 0;
        var formula = manager.GetFormula((uint)FormulaKind.CastingDelayTime);
        if (formula == null)
            return 0;
        var result = formula.Evaluate(new Dictionary<string, double>
        {
            ["casting_tolerance"] = tolerance,
            ["damage_percent"] = damage * 100d / unit.MaxHp
        });
        return double.IsFinite(result) ? (int)Math.Clamp(result, 0, int.MaxValue) : 0;
    }
}
