using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.NpcGroup;

public sealed partial class NpcGroupInstance
{
    internal bool TryCopyEffectThreat(Npc recipient, Unit target, int damage, int heal)
    {
        if (Template.AggroRuleId != (int)NpcGroupAggroRuleKind.AggroShare)
            return false;
        var members = GetMembers().Where(npc => CanShare(npc, target)).ToArray();
        if (!members.Contains(recipient))
            return true;
        List<Action> notifications = [];
        lock (_combatSync)
        {
            if (!IsCurrentMember(recipient, target))
                return true;
            var threat = GetSharedThreat(target);
            threat.Damage = damage;
            threat.Heal = heal;
            CopyThreat(members, threat, notifications);
        }
        Notify(notifications);
        return true;
    }
}
