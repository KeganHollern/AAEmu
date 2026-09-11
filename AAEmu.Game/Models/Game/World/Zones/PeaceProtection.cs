using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.World.Zones;

public static class PeaceProtection
{
    // These are native protection selectors, not rows in system_factions.
    private const uint BothAlliances = 4;
    private const uint AllPlayerOwners = 5;

    public static bool PreventsAttack(BaseUnit attacker, BaseUnit target)
    {
        if (attacker == null || target == null || attacker == target)
            return false;

        var attackerOwnerId = GetPlayerOwnerId(attacker);
        var targetOwnerId = GetPlayerOwnerId(target);
        if (attackerOwnerId == 0 || targetOwnerId == 0 || attackerOwnerId == targetOwnerId)
            return false;

        var attackerProtected = IsProtected(attacker);
        var targetProtected = IsProtected(target);
        if (!attackerProtected && !targetProtected)
            return false;

        if (attacker is Character or Units.Mate && target is Character or Units.Mate &&
            DuelManager.Instance.AreActiveOpponents(attacker.GetOwnerCharacter(), target.GetOwnerCharacter()))
            return false;

        // Native relation evaluation applies the level guard before retaliation
        // and Retribution. The native character-ID check excludes owned units.
        if (attackerProtected && IsLowLevel(attacker) || targetProtected && IsLowLevel(target))
            return true;

        if (!targetProtected)
            return false;

        if (attacker.GetRelationStateTo(target) == RelationState.Hostile)
        {
            // The native retaliation query belongs to the attacking character
            // and uses the target object, not merely the target owner's faction.
            return attacker is not Character { IsInBattle: true } character || !character.IsActivelyHostile(target);
        }

        // Friendly/neutral targets lose protection through Retribution. Forced
        // attack alone does not override the Peace selector.
        return target is not (Character or Units.Mate or Slave) ||
               target.GetOwnerCharacter()?.Buffs.CheckBuff((uint)BuffConstants.Retribution) != true;
    }

    private static bool IsLowLevel(BaseUnit unit) => unit is Character { Level: <= 10 };

    private static uint GetPlayerOwnerId(BaseUnit unit) => unit switch
    {
        Character character => character.Id,
        House house => house.OwnerId,
        Shipyard.Shipyard shipyard => shipyard.ShipyardData?.Type2 ?? 0,
        Slave { OwnerId: > 0 } slave => slave.OwnerId,
        _ => unit.GetOwnerCharacter()?.Id ?? 0
    };

    private static bool IsProtected(BaseUnit unit)
    {
        var zone = ZoneManager.Instance.GetZoneByKey(unit.Transform.ZoneId);
        var conflict = zone == null ? null : ZoneManager.Instance.GetZoneGroupById(zone.GroupId)?.Conflict;
        if (conflict == null || conflict.Closed || conflict.CurrentZoneState != ZoneConflictType.Peace)
            return false;

        var factionId = conflict.PeaceProtectedFactionId;
        if (factionId == AllPlayerOwners)
            return true;
        if (factionId == 0 || unit.Faction == null)
            return false;
        if (factionId == BothAlliances)
            return IsFriendlyTo(unit.Faction, FactionsEnum.NuiaAlliance) ||
                   IsFriendlyTo(unit.Faction, FactionsEnum.HaranyaAlliance);

        return IsFriendlyTo(unit.Faction, (FactionsEnum)factionId);
    }

    private static bool IsFriendlyTo(SystemFaction faction, FactionsEnum protectedFaction)
    {
        return faction.GetRelationState(FactionManager.Instance.GetFaction(protectedFaction)) == RelationState.Friendly;
    }
}
