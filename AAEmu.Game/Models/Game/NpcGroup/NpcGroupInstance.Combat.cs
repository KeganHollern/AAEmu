using System.Runtime.CompilerServices;

using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.NpcGroup;

public sealed partial class NpcGroupInstance
{
    private readonly object _combatSync = new();
    private readonly Dictionary<uint, SharedThreat> _sharedThreat = [];
    private readonly ConditionalWeakTable<OnHealedArgs, object> _sharedHealEvents = new();

    private sealed class SharedThreat(Unit target)
    {
        public Unit Target { get; } = target;
        public int Damage { get; set; }
        public int Heal { get; set; }
    }

    // Lock order: group combat, membership snapshot, local aggro table, aggro value.
    // Callbacks run only after all these locks are released.
    internal void AddSharedThreat(Npc source, Unit target, Aggro expected, int damage, int heal)
    {
        if (Template.AggroRuleId != (int)NpcGroupAggroRuleKind.AggroShare)
            return;
        // Buff checks can take buff locks whose callbacks enter combat. Read them
        // before _combatSync; recheck lifetime and identity inside the mutation gate.
        var members = GetMembers().Where(npc => CanShare(npc, target)).ToArray();
        if (!members.Contains(source))
            return;
        List<Action> notifications = [];
        lock (_combatSync)
        {
            lock (source.AggroTable)
            {
                // A delayed add must not recreate a target that cleanup already removed.
                if (!source.AggroTable.TryGetValue(target.ObjId, out var current) ||
                    !ReferenceEquals(current, expected) || !IsCurrentMember(source, target))
                    return;
                var threat = GetSharedThreat(target);
                threat.Damage += damage;
                threat.Heal += heal;
                CopyThreat(members, threat, notifications);
            }
        }
        Notify(notifications);
    }

    internal bool HandleSharedHealing(Npc observer, OnHealedArgs args)
    {
        if (Template.AggroRuleId != (int)NpcGroupAggroRuleKind.AggroShare)
            return false;
        if (args?.Healer == null || !CanShare(observer, args.Healer) || observer.AggroTable.IsEmpty)
            return true;
        var members = GetMembers().Where(npc => CanShare(npc, args.Healer)).ToArray();
        var amounts = members.ToDictionary(npc => npc, npc =>
        {
            var amount = (int)(args.HealAmount * (args.Healer.AggroMul / 100.0f));
            amount = (int)(amount * (npc.IncomingAggroMul / 100.0f));
            return (int)(amount * 0.6f);
        });
        List<Action> notifications = [];
        lock (_combatSync)
        {
            if (_sharedHealEvents.TryGetValue(args, out _))
                return true;
            // Stable row order makes member multipliers independent of event subscription order.
            var source = members.FirstOrDefault(npc => !npc.AggroTable.IsEmpty && IsCurrentMember(npc, args.Healer));
            if (source == null)
                return true;
            lock (source.AggroTable)
            {
                if (source.AggroTable.IsEmpty || !IsCurrentMember(source, args.Healer))
                    return true;
                _sharedHealEvents.Add(args, new object());
                var threat = GetSharedThreat(args.Healer);
                threat.Heal += amounts[source];
                CopyThreat(members, threat, notifications, source);
            }
        }
        Notify(notifications);
        return true;
    }

    public void SynchronizeCombat(Npc npc)
    {
        if (Template.AggroRuleId != (int)NpcGroupAggroRuleKind.AggroShare)
            return;
        SharedThreat[] snapshot;
        lock (_combatSync)
        {
            PruneSharedThreatLocked();
            snapshot = _sharedThreat.Values.ToArray();
        }
        var eligible = snapshot.Where(threat => CanShare(npc, threat.Target)).ToArray();
        List<Action> notifications = [];
        lock (_combatSync)
        {
            foreach (var threat in eligible)
                if (_sharedThreat.TryGetValue(threat.Target.ObjId, out var current) && ReferenceEquals(current, threat))
                    CopyThreat([npc], threat, notifications);
        }
        Notify(notifications);
    }

    internal void PruneSharedThreat()
    {
        lock (_combatSync)
            PruneSharedThreatLocked();
    }

    internal void ClearSharedThreat()
    {
        lock (_combatSync)
        {
            _sharedThreat.Clear();
            _sharedHealEvents.Clear();
        }
    }

    private void PruneSharedThreatLocked()
    {
        var members = GetMembers();
        foreach (var (id, threat) in _sharedThreat.ToArray())
        {
            if (IsRetired || !members.Any(npc => IsCurrentMember(npc, threat.Target) &&
                npc.AggroTable.TryGetValue(id, out var aggro) && ReferenceEquals(aggro.Owner, threat.Target)))
                _sharedThreat.Remove(id);
        }
    }

    private SharedThreat GetSharedThreat(Unit target)
    {
        if (!_sharedThreat.TryGetValue(target.ObjId, out var threat) || !ReferenceEquals(threat.Target, target))
            _sharedThreat[target.ObjId] = threat = new SharedThreat(target);
        return threat;
    }

    private void CopyThreat(Npc[] members, SharedThreat threat, List<Action> notifications, Npc sourceEffect = null)
    {
        foreach (var member in members)
        {
            if (!IsCurrentMember(member, threat.Target))
                continue;
            var complete = member.SetSharedAggro(threat.Target, threat.Damage, threat.Heal,
                ReferenceEquals(member, sourceEffect), this);
            if (complete == null)
                continue;
            notifications.Add(() =>
            {
                if (!CanShare(member, threat.Target))
                    return;
                complete();
                var ai = member.Ai;
                if (ai != null && CanShare(member, threat.Target) &&
                    ReferenceEquals(member.Ai, ai) && ReferenceEquals(ai.Owner, member))
                    ai.RequestAggroTargetUpdate();
            });
        }
    }

    private bool CanShare(Npc npc, Unit target)
    {
        return IsCurrentMember(npc, target) &&
            !npc.Buffs.CheckBuffTag((uint)TagsEnum.NoFight) &&
            !npc.Buffs.CheckBuffTag((uint)TagsEnum.Returning) &&
            !target.Buffs.CheckBuffTag((uint)TagsEnum.NoFight) &&
            !target.Buffs.CheckBuffTag((uint)TagsEnum.Returning) && npc.CanAttack(target);
    }

    private bool IsCurrentMember(Npc npc, Unit target)
    {
        return !IsRetired && npc != null && ReferenceEquals(npc.GroupInstance, this) &&
            IsCurrentAlive(npc) && IsCurrentAlive(target) &&
            ReferenceEquals(npc.ParentWorld, target.ParentWorld) && !ReferenceEquals(npc, target) &&
            ReferenceEquals(npc.Ai?.Owner, npc) &&
            npc.Ai?.GetCurrentBehavior() is not (ReturnStateBehavior or DeadBehavior);
    }

    private static bool IsCurrentAlive(Unit unit)
    {
        return unit is { Hp: > 0 } && !unit.IsDead && unit is not (Npc { Despawned: true } or Npc { CombatRetired: true }) &&
            ReferenceEquals(unit.ParentWorld?.GetUnit(unit.ObjId), unit);
    }

    private static void Notify(List<Action> notifications)
    {
        foreach (var notification in notifications)
            notification();
    }
}
