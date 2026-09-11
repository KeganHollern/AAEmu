namespace AAEmu.Game.Models.Account;

public static class PermissionPolicy
{
    public static bool Allows(AccountRole role, GamePermission permission)
    {
        if (!Enum.IsDefined(role) || !Enum.IsDefined(permission))
            return false;

        return permission switch
        {
            GamePermission.PlayerCommands or GamePermission.PlayerGameplay => true,
            GamePermission.ManageRoles or GamePermission.ManageServer or
                GamePermission.EditWorld or GamePermission.SendRawPackets => role == AccountRole.Admin,
            _ => role is AccountRole.Moderator or AccountRole.Admin
        };
    }

    public static bool CanModerate(AccountRole actor, AccountRole target)
    {
        if (!Enum.IsDefined(actor) || !Enum.IsDefined(target))
            return false;

        return actor == AccountRole.Admin ||
            actor == AccountRole.Moderator && target == AccountRole.NormalPlayer;
    }
}
