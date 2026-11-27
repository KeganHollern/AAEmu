using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Buffs.Triggers;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    // The client reports the closure before its next position. Allow two normal 100 ms
    // movement intervals and a scheduling margin. A later packet must not root a new jump.
    private const double GliderMovementWindow = 0.25;
    private sealed record GliderLanding(Buff Flight, BuffTriggerTemplate Contact, WorldInstance World,
        Vector3 Position, double Time);
    private GliderLanding _gliderLanding;

    internal void CancelGliderLanding() => Interlocked.Exchange(ref _gliderLanding, null);

    internal bool TryCloseGliderForLanding(Buff flight, double? time = null)
    {
        if (flight is not { InUse: true, State: EffectState.Acting, Template: { Kind: BuffKind.Good, Gliding: true, RemoveOnLand: true } } ||
            flight.Owner != this || Buffs.GetEffectByIndex(flight.Index) != flight || flight.Template.GlidingLandHeight <= 0 ||
            (flight.Duration > 0 && flight.GetTimeLeft() <= 0))
            return false;

        var position = Transform.World.Position;
        if (!TryGetDryGliderGap(position, out var gap))
            return false;
        var maximumStep = flight.Template.GlidingFallSpeedFast * GliderMovementWindow;
        if (gap > flight.Template.GlidingLandHeight + maximumStep + FallMovement.PositionTolerance)
            return false;

        var triggers = SkillManager.Instance.GetBuffTriggerTemplates(flight.Template.BuffId);
        // Some stock flight buffs also remove an NPC or start a bubble at timeout.
        // Only substitute the common glider cooldown for the two generic exit events.
        if (triggers.Any(trigger => trigger.Kind is BuffEventTriggerKind.Timeout or BuffEventTriggerKind.Dispelled &&
            trigger.Effect is not BuffEffect { Buff.Id: (uint)BuffConstants.PreparingGlider }))
            return false;
        var landing = triggers.Where(trigger => trigger.Kind == BuffEventTriggerKind.Landing).ToArray();
        var contact = landing.FirstOrDefault(trigger => trigger.Effect is BuffEffect { Buff.Id: (uint)BuffConstants.GliderLanding });
        if (contact == null || landing.Any(trigger => trigger != contact && trigger.Effect is not BuffEffect { Buff.Id: (uint)BuffConstants.PreparingGlider }))
            return false;

        var touchdown = gap <= flight.Template.GlidingLandHeight + FallMovement.PositionTolerance;
        var pending = touchdown ? null : new GliderLanding(flight, contact, ParentWorld, position, time ?? FallMovement.Now);
        Interlocked.Exchange(ref _gliderLanding, pending);
        if (!flight.TryFinishGliderLanding())
        {
            Interlocked.CompareExchange(ref _gliderLanding, null, pending);
            return false;
        }
        foreach (var trigger in landing)
        {
            if (trigger != contact)
                new BuffTrigger(flight, trigger).Execute(flight, EventArgs.Empty);
        }

        if (touchdown)
            new BuffTrigger(flight, contact).Execute(flight, EventArgs.Empty);
        return true;
    }

    internal void ObserveGliderLanding(double time)
    {
        if (Volatile.Read(ref _gliderLanding) == null)
            return;
        var landing = Interlocked.Exchange(ref _gliderLanding, null);
        if (landing == null || landing.World != ParentWorld || time < landing.Time || time - landing.Time > GliderMovementWindow)
            return;

        var position = Transform.World.Position;
        var step = position - landing.Position;
        var template = landing.Flight.Template;
        var maxTravel = template.GlidingMoveSpeedFast * GliderMovementWindow + FallMovement.PositionTolerance;
        var maxDescent = template.GlidingFallSpeedFast * GliderMovementWindow + FallMovement.PositionTolerance;
        if (step.Z > FallMovement.PositionTolerance || -step.Z > maxDescent ||
            step.X * step.X + step.Y * step.Y > maxTravel * maxTravel ||
            !TryGetDryGliderGap(position, out var gap) || gap > template.GlidingLandHeight + FallMovement.PositionTolerance)
            return;

        new BuffTrigger(landing.Flight, landing.Contact).Execute(landing.Flight, EventArgs.Empty);
    }

    private bool TryGetDryGliderGap(Vector3 position, out float gap)
    {
        gap = 0;
        if (DisabledSetPosition || IsDead || IsRiding || ActiveSkillController != null || DisplacedUntil > DateTime.UtcNow ||
            Transform.Parent != null || Transform.StickyParent != null ||
            ParentWorld?.Template?.TryGetFallTerrainHeight(position.X, position.Y, out var ground) != true ||
            ParentWorld.IsWater(new Vector3(position.X, position.Y, ground)))
            return false;

        gap = position.Z - ground;
        return float.IsFinite(gap) && gap >= -FallMovement.PositionTolerance;
    }
}
