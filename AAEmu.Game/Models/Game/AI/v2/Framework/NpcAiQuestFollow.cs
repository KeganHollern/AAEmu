using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.AI.v2.Framework;

public abstract partial class NpcAi
{
    private readonly object _questFollowSyncRoot = new();
    private object _questFollowOwner;
    private Unit _questFollowTarget;
    private Action _questFollowLost;
    private Vector3 _questFollowIdlePosition;
    private int _questFollowRestorePending;

    internal bool TryStartQuestFollow(object owner, Unit target, Action onLost)
    {
        lock (_questFollowSyncRoot)
        {
            if (_questFollowOwner != null)
                return IsQuestFollowOwner(owner) && ReferenceEquals(_questFollowTarget, target);
            if (owner == null || !IsValidFollowTarget(target) || AiFollowUnitObj != null ||
                !_behaviors.ContainsKey(BehaviorKind.FollowUnit) ||
                AiCurrentCommand != null || AiCommandsQueue.Count > 0 ||
                (_currentBehavior != null && _currentBehavior == GetBehavior(BehaviorKind.FollowPath)))
                return false;

            _questFollowOwner = owner;
            _questFollowTarget = target;
            _questFollowLost = onLost;
            _questFollowIdlePosition = IdlePosition;
            Interlocked.Exchange(ref _questFollowRestorePending, 0);
            AiFollowUnitObj = target;
            Owner.Events.AddDeathHandler(OnQuestFollowNpcDied);
            Owner.Removing += OnQuestFollowNpcRemoved;
            GoToFollowUnit();
            return true;
        }
    }

    internal bool IsQuestFollowOwner(object owner)
    {
        lock (_questFollowSyncRoot)
            return ReferenceEquals(_questFollowOwner, owner) && _questFollowOwner != null &&
                ReferenceEquals(AiFollowUnitObj, _questFollowTarget) && IsValidFollowTarget(_questFollowTarget) &&
                AiCurrentCommand == null && AiCommandsQueue.Count == 0;
    }

    internal void StopQuestFollow(object owner)
    {
        lock (_questFollowSyncRoot)
        {
            if (_questFollowOwner == null || !ReferenceEquals(_questFollowOwner, owner))
                return;

            ReleaseQuestFollow(AiCurrentCommand == null && AiCommandsQueue.Count == 0 &&
                Owner?.IsInPatrol != true);
        }
    }

    private bool IsValidFollowTarget(Unit target)
    {
        return Owner is { IsDead: false, Despawned: false, CombatRetired: false, IsInPatrol: false, Hp: > 0 } npc &&
            ReferenceEquals(npc.Ai, this) && target is { IsDead: false, Hp: > 0 } &&
            npc.ParentWorld != null && ReferenceEquals(npc.ParentWorld, target.ParentWorld) &&
            npc.Transform != null && target.Transform != null &&
            npc.Transform.WorldId == target.Transform.WorldId &&
            npc.Transform.InstanceId == target.Transform.InstanceId &&
            (target is not Character character || character.IsOnline);
    }

    private bool ResumeQuestFollow()
    {
        lock (_questFollowSyncRoot)
        {
            if (_questFollowOwner == null || !ReferenceEquals(AiFollowUnitObj, _questFollowTarget) ||
                !IsValidFollowTarget(_questFollowTarget) || AiCurrentCommand != null || AiCommandsQueue.Count > 0)
                return false;
            GoToFollowUnit();
            return true;
        }
    }

    private void CheckQuestFollow()
    {
        Action onLost = null;
        lock (_questFollowSyncRoot)
        {
            if (_questFollowOwner == null)
                return;
            var controlReplaced = AiCurrentCommand != null || AiCommandsQueue.Count > 0 || Owner?.IsInPatrol == true;
            if (!controlReplaced && ReferenceEquals(AiFollowUnitObj, _questFollowTarget) &&
                IsValidFollowTarget(_questFollowTarget))
                return;
            onLost = _questFollowLost;
            ReleaseQuestFollow(!controlReplaced);
        }
        onLost?.Invoke();
    }

    private void RestoreQuestFollowOnTick()
    {
        if (Interlocked.Exchange(ref _questFollowRestorePending, 0) == 0)
            return;
        lock (_questFollowSyncRoot)
        {
            if (_questFollowOwner != null || AiFollowUnitObj != null ||
                _currentBehavior != GetBehavior(BehaviorKind.FollowUnit) ||
                AiCurrentCommand != null || AiCommandsQueue.Count > 0 ||
                Owner is not { IsDead: false, Despawned: false, CombatRetired: false, IsInPatrol: false, Hp: > 0 } npc ||
                !ReferenceEquals(npc.Ai, this))
                return;
        }
        // Cleanup queues restoration for the owning AI tick, which checks current control.
        GoToIdle();
    }

    private void CancelQuestFollowForControl()
    {
        Action onLost;
        lock (_questFollowSyncRoot)
        {
            if (_questFollowOwner == null)
                return;
            onLost = _questFollowLost;
            ReleaseQuestFollow(false);
        }
        onLost?.Invoke();
    }

    private void ReleaseQuestFollow(bool restoreIdle = true)
    {
        var npc = Owner;
        if (npc != null)
        {
            npc.Events.RemoveDeathHandler(OnQuestFollowNpcDied);
            npc.Removing -= OnQuestFollowNpcRemoved;
        }
        var ownsTarget = ReferenceEquals(AiFollowUnitObj, _questFollowTarget);
        if (ownsTarget)
        {
            AiFollowUnitObj = null;
            if (restoreIdle)
                IdlePosition = _questFollowIdlePosition;
        }
        _questFollowOwner = null;
        _questFollowTarget = null;
        _questFollowLost = null;

        // Do not interrupt combat, death, despawn, or another control command.
        if (restoreIdle && ownsTarget && npc is { IsDead: false, Despawned: false, CombatRetired: false, Hp: > 0 } &&
            ReferenceEquals(npc.Ai, this) && _currentBehavior == GetBehavior(BehaviorKind.FollowUnit))
            Interlocked.Exchange(ref _questFollowRestorePending, 1);
    }

    private void OnQuestFollowNpcDied(object sender, OnDeathArgs args)
    {
        if (ReferenceEquals(args.Victim, Owner))
            OnQuestFollowNpcRemoved(Owner);
    }

    private void OnQuestFollowNpcRemoved(Npc npc)
    {
        Action onLost;
        lock (_questFollowSyncRoot)
        {
            if (_questFollowOwner == null)
                return;
            onLost = _questFollowLost;
            ReleaseQuestFollow();
        }
        // Removal can hold the spawner lock. The callback only queues quest evaluation.
        onLost?.Invoke();
    }
}
