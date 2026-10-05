using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Units;

public partial class Unit
{
    /// <summary>
    /// Imports resolved threat without replaying damage, modifiers, or quest credit.
    /// </summary>
    internal void CopyThreatFrom(Npc source)
    {
        if (this is not Npc recipient || ReferenceEquals(source, recipient) || source == null ||
            !ReferenceEquals(source.ParentWorld, ParentWorld) ||
            !ReferenceEquals(ParentWorld?.GetUnit(source.ObjId), source) ||
            source.IsDead || source.Despawned || source.CombatRetired || source.Hp <= 0)
            return;

        (Unit Owner, int Damage, int Heal)[] snapshot;
        lock (source.AggroTable)
            snapshot = source.AggroTable.Values
                .Select(aggro => (aggro.Owner, aggro.DamageAggro, aggro.HealAggro)).ToArray();
        foreach (var (owner, damage, heal) in snapshot)
        {
            if (damage + (long)heal <= 0 ||
                recipient.Buffs.CheckBuffTag((uint)TagsEnum.NoFight) ||
                recipient.Buffs.CheckBuffTag((uint)TagsEnum.Returning) ||
                owner.Buffs.CheckBuffTag((uint)TagsEnum.NoFight) ||
                owner.Buffs.CheckBuffTag((uint)TagsEnum.Returning) || !recipient.CanAttack(owner))
                continue;
            if (recipient.GroupInstance?.TryCopyEffectThreat(recipient, owner, damage, heal) == true)
                continue;
            var complete = recipient.SetSharedAggro(owner, damage, heal);
            if (complete == null)
                continue;
            complete();
            recipient.Ai?.RequestAggroTargetUpdate();
        }
    }

    /// <summary>
    /// Removes this exact unit from same-world NPC threat and selection state.
    /// </summary>
    internal void RemoveIncomingThreat()
    {
        if (ParentWorld == null)
            return;
        foreach (var npc in ParentWorld.GetAllNpcs())
        {
            if (npc.AggroTable.TryGetValue(ObjId, out var aggro) && ReferenceEquals(aggro.Owner, this))
                npc.ClearAggroOfUnit(this);
            var selected = ReferenceEquals(npc.CurrentTarget, this);
            var currentAggro = ReferenceEquals(npc.CurrentAggroTarget, this);
            if (!selected && !currentAggro)
                continue;
            if (currentAggro)
                npc.CurrentAggroTarget = null;
            if (selected)
                npc.SetTarget(null);
            npc.Ai?.RequestAggroTargetUpdate();
        }
    }
}
