using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers;

public class PermissionManager(IAccountManager accountManager) : Singleton<PermissionManager>, IPermissionManager
{
    public AccountRole GetRole(ICharacter character)
    {
        return GetRole(character?.AccountId ?? 0);
    }

    public AccountRole GetRole(uint accountId)
    {
        if (accountId == 0)
            return AccountRole.NormalPlayer;

        var role = accountManager.GetAccountRole(accountId);
        return Enum.IsDefined(role) ? role : AccountRole.NormalPlayer;
    }

    public bool CanUse(ICharacter character, GamePermission permission)
    {
        return PermissionPolicy.Allows(GetRole(character), permission);
    }

    public bool CanModerate(uint actorAccountId, uint targetAccountId)
    {
        return actorAccountId != 0 && targetAccountId != 0 &&
            accountManager.TryGetAccountRole(actorAccountId, out var actorRole) &&
            accountManager.TryGetAccountRole(targetAccountId, out var targetRole) &&
            PermissionPolicy.CanModerate(actorRole, targetRole);
    }
}
