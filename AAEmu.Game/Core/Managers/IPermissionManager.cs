using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers;

public interface IPermissionManager
{
    AccountRole GetRole(ICharacter character);
    AccountRole GetRole(uint accountId);
    bool CanUse(ICharacter character, GamePermission permission);
    bool CanModerate(uint actorAccountId, uint targetAccountId);
}
