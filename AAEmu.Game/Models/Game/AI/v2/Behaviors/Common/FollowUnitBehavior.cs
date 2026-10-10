using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;

public class FollowUnitBehavior : BaseCombatBehavior
{
    private bool _enter;

    public override void Enter()
    {
        Ai.Owner.CurrentGameStance = GameStanceType.Relaxed;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Idle;
        _enter = true;
    }

    public override void Tick(TimeSpan delta)
    {
        var owner = Ai.Owner;
        var target = Ai.AiFollowUnitObj;
        if (!_enter || owner == null || !ReferenceEquals(owner.Ai, Ai) || owner.IsInPatrol)
            return;

        if (!UpdateTarget())
            owner.SetTarget(null);

        if (CheckAggression())
            return;

        if (CheckAlert())
            return;

        if (target == null || target.Hp <= 0 || target.IsDead || target.ParentWorld != owner.ParentWorld ||
            target.Transform.WorldId != owner.Transform.WorldId || target.Transform.InstanceId != owner.Transform.InstanceId)
        {
            if (!ReferenceEquals(Ai.AiFollowUnitObj, target))
                return;
            Ai.AiFollowUnitObj = null;
            Ai.GoToIdle();
            return;
        }

        var targetDistance = owner.GetDistanceTo(target, true);
        // Drop or disconnect can release control during the earlier combat checks.
        if (!_enter || !ReferenceEquals(Ai.Owner, owner) || !ReferenceEquals(owner.Ai, Ai) ||
            !ReferenceEquals(Ai.AiFollowUnitObj, target) || owner.IsInPatrol)
            return;
        var followSpeedMultiplier = (float)Math.Min(5.0, targetDistance / 1.5);

        var moveSpeed = Ai.GetRealMovementSpeed(owner.BaseMoveSpeed) * followSpeedMultiplier;
        var moveFlags = Ai.GetRealMovementFlags(moveSpeed);
        moveSpeed *= delta.TotalSeconds;
        owner.MoveTowards(target.Transform.World.Position, (float)moveSpeed, moveFlags);
        Ai.IdlePosition = owner.Transform.World.Position;
    }

    public override void Exit()
    {
        _enter = false;
    }
}
