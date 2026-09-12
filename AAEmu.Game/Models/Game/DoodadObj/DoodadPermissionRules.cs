using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj;

public static class DoodadPermissionRules
{
    public static bool Demand(BaseUnit caster, Doodad doodad, uint permission)
    {
        if (Allows(caster, doodad, permission))
            return true;
        SkillLaborBatch.For(caster as Character)?.Fail();
        (caster as Unit)?.SendErrorMessage(ErrorMessageType.InteractionPermissionDeny);
        return false;
    }

    public static bool Allows(BaseUnit caster, Doodad doodad, uint permission)
    {
        if (permission == (uint)DoodadFuncPermission.Any)
            return true;
        if (permission > (uint)DoodadFuncPermission.ZoneResidents)
            return false;
        var player = caster?.GetOwnerCharacter();
        if (player == null || player.Id == 0)
            return false;

        var house = doodad.OwnerType == DoodadOwnerType.Housing
            ? HousingManager.Instance.GetHouseById(doodad.OwnerDbId) : null;
        var ownerId = doodad.OwnerType switch
        {
            DoodadOwnerType.Housing => house?.OwnerId ?? 0,
            DoodadOwnerType.Slave => doodad.ParentWorld?.GetSlaveByObjId(doodad.ParentObjId)?.OwnerId ?? 0,
            _ => doodad.OwnerId
        };
        switch ((DoodadFuncPermission)permission)
        {
            case DoodadFuncPermission.OwnerOnly:
                return ownerId != 0 && ownerId == player.Id;
            case DoodadFuncPermission.OwnerFamily:
                return ownerId != 0 && (ownerId == player.Id || (player.Family != 0 &&
                    FamilyManager.Instance.GetFamilyOfCharacter(ownerId) == player.Family));
            case DoodadFuncPermission.SiegeMaster:
                var guild = player.Expedition;
                var member = guild?.Members.FirstOrDefault(member => member.CharacterId == player.Id);
                return member != null && (member.Role == byte.MaxValue ||
                    guild.Policies.Any(policy => policy.Role == member.Role && policy.SiegeMaster)) &&
                    (doodad.Type2 == 0 || doodad.Type2 == (uint)guild.Id);
            case DoodadFuncPermission.OwnerParty:
                return IsOwnerTeamMember(player, ownerId, true);
            case DoodadFuncPermission.OwnerRaidMembers:
                return IsOwnerTeamMember(player, ownerId, false);
            case DoodadFuncPermission.SameAccount:
                var ownerAccount = house?.AccountId ?? NameManager.Instance.GetCharacterAccount(ownerId);
                return ownerId != 0 && (ownerId == player.Id ||
                    (ownerAccount != 0 && ownerAccount == player.AccountId));
            case DoodadFuncPermission.DominionNation:
                // DominionManager permits only unclaimed state until issue #143 adds the claim lifecycle.
                // No nation owns any of these territories, so none grants this permission.
                return false;
            case DoodadFuncPermission.ZoneResidents:
                var zoneGroup = ZoneManager.Instance.GetZoneByKey(doodad.Transform.ZoneId)?.GroupId ?? 0;
                if (zoneGroup == 0 || player.AccountId == 0)
                    return false;
                var houses = new Dictionary<uint, House>();
                HousingManager.Instance.GetByAccountId(houses, player.AccountId);
                return houses.Values.Any(residence =>
                    ZoneManager.Instance.GetZoneByKey(residence.Transform.ZoneId)?.GroupId == zoneGroup);
            default:
                return false;
        }
    }

    private static bool IsOwnerTeamMember(Character player, uint ownerId, bool sameParty)
    {
        if (ownerId == 0)
            return false;
        if (ownerId == player.Id)
            return true;
        var team = TeamManager.Instance.GetActiveTeamByUnit(player.Id);
        if (team == null)
            return false;
        lock (team.SyncLock)
        {
            var playerIndex = Array.FindIndex(team.Members, member => member?.Character?.Id == player.Id);
            var ownerIndex = Array.FindIndex(team.Members, member => member?.Character?.Id == ownerId);
            return playerIndex >= 0 && ownerIndex >= 0 &&
                (!sameParty || team.IsParty || playerIndex / 5 == ownerIndex / 5);
        }
    }
}
