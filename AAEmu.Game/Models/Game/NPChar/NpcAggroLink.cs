using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.NPChar;

/// <summary>
/// Authored assistance between nearby NPCs. Link membership is symmetric, but each
/// receiver independently controls whether, and from how far away, it accepts help requests.
/// Runtime NPC-group aggro rules use their own occurrence ownership.
/// </summary>
internal static class NpcAggroLink
{
    internal static bool TryHelp(Npc source, Npc helper, Unit abuser)
    {
        return CanHelp(source, helper, abuser) && StartAssistance(helper, abuser);
    }

    internal static void RequestGuardHelp(Character victim, Character attacker)
    {
        if (victim.Faction?.GuardHelp != true || victim == attacker || attacker.Faction == null || !IsCurrentAlive(attacker) ||
            !ReferenceEquals(victim.ParentWorld, attacker.ParentWorld) ||
            !ReferenceEquals(victim.ParentWorld.GetUnit(victim.ObjId), victim))
            return;

        const float scanRadius = 50f;
        foreach (var guard in WorldManager.GetAround<Npc>(victim, scanRadius))
        {
            if (guard.Template is not { AggroLinkSpecialGuard: true, AcceptAggroLink: true } template ||
                guard.Faction == null || guard.IsInBattle || !guard.AggroTable.IsEmpty || !IsCurrentAlive(guard) ||
                !ReferenceEquals(guard.ParentWorld, victim.ParentWorld))
                continue;

            var distanceSquared = Vector3.DistanceSquared(guard.Transform.World.Position, victim.Transform.World.Position);
            if (distanceSquared > scanRadius * scanRadius)
                continue;

            // A sight-based guard must see the victim. Distance-based guards use their authored help range.
            var inRange = template.AggroLinkSightCheck ? guard.CanSeeTarget(victim) :
                float.IsFinite(template.AggroLinkHelpDist) && guard.GetDistanceTo(victim, true) <= template.AggroLinkHelpDist;
            if (inRange && AcceptsFactionRule(guard, victim) && guard.CanAttack(attacker))
                StartAssistance(guard, attacker);
        }
    }

    private static bool StartAssistance(Npc helper, Unit abuser)
    {
        var ai = helper.Ai;
        if (ai == null || !ReferenceEquals(ai.Owner, helper))
            return false;

        // The initial-entry gate belongs to the combat mutation. It must remain atomic
        // without an outer lock across group propagation or quest/AI callbacks.
        if (!helper.TryAddAssistanceAggro(abuser))
            return false;

        if (ReferenceEquals(helper.Ai, ai) && ReferenceEquals(ai.Owner, helper))
            ai.RequestAggroTargetUpdate();
        return true;
    }

    internal static bool CanHelp(Npc source, Npc helper, Unit abuser)
    {
        var sourceAi = source?.Ai;
        var helperAi = helper?.Ai;
        if (source?.Template == null || helper?.Template == null ||
            ReferenceEquals(source, helper) || ReferenceEquals(helper, abuser) ||
            !IsCurrentAlive(source) || !IsCurrentAlive(helper) || !IsCurrentAlive(abuser) ||
            !ReferenceEquals(source.ParentWorld, helper.ParentWorld) ||
            !ReferenceEquals(source.ParentWorld, abuser.ParentWorld) ||
            sourceAi == null || !ReferenceEquals(sourceAi.Owner, source) ||
            helperAi == null || !ReferenceEquals(helperAi.Owner, helper) ||
            helper.IsInBattle || !helper.AggroTable.IsEmpty || !helper.Template.AcceptAggroLink)
            return false;

        var distance = helper.Template.AggroLinkHelpDist;
        if (!float.IsFinite(distance) || distance < 0 || helper.GetDistanceTo(source) > distance)
            return false;

        if (helper.Template.AggroLinkSightCheck && !helper.CanSeeTarget(abuser))
            return false;
        if (!helper.CanAttack(abuser))
            return false;

        if (source.GroupInstance is { IsRetired: false } group &&
            ReferenceEquals(helper.GroupInstance, group) &&
            (group.Template.AggroRuleId == (int)NpcGroupAggroRuleKind.AggroLink ||
             group.Template.AggroRuleId == (int)NpcGroupAggroRuleKind.AggroShare))
            return true;
        if (NpcAggroLinkGameData.Instance.AreLinked(source.TemplateId, helper.TemplateId))
            return true;

        return AcceptsFactionRule(helper, source);
    }

    private static bool AcceptsFactionRule(Npc helper, Unit source)
    {
        return helper.Template.AggroLinkSpecialRuleId switch
        {
            AggroLinkSpecialRuleKind.FactionHelp =>
                helper.Faction != null && source.Faction != null && helper.Faction.Id == source.Faction.Id,
            AggroLinkSpecialRuleKind.FriendlyHelp => helper.GetRelationStateTo(source) == RelationState.Friendly,
            AggroLinkSpecialRuleKind.NeutralHelp => helper.GetRelationStateTo(source) == RelationState.Neutral,
            AggroLinkSpecialRuleKind.EveryoneHelp => true,
            _ => false
        };
    }

    private static bool IsCurrentAlive(Unit unit)
    {
        var world = unit?.ParentWorld;
        return unit is { Hp: > 0 } && !unit.IsDead && world != null &&
               unit is not (Npc { Despawned: true } or Npc { CombatRetired: true }) && ReferenceEquals(world.GetUnit(unit.ObjId), unit);
    }
}
