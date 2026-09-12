namespace AAEmu.Game.Models.Game.DoodadObj.Static;

// Exact r208022 client: x2game.dll FUN_393b3300. See Docs/customized/doodad-function-permissions.md.
public enum DoodadFuncPermission : byte
{
    Any = 0,
    OwnerOnly = 1,
    OwnerFamily = 2,
    SiegeMaster = 3,
    OwnerParty = 4,
    OwnerRaidMembers = 5,
    SameAccount = 6,
    DominionNation = 7,
    ZoneResidents = 8
}
