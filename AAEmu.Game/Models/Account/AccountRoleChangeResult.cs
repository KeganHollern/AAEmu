namespace AAEmu.Game.Models.Account;

public enum AccountRoleChangeStatus
{
    Completed,
    Rejected,
    Unconfirmed
}

public sealed record AccountRoleChangeResult(AccountRoleChangeStatus Status, string Detail);
