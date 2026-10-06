using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncStoreUi : DoodadFuncTemplate
{
    // doodad_funcs
    public uint MerchantPackId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        // r208022 opens the store locally (native 393b8cc0). CSBuyItems validates
        // the current function, merchant pack, permission, and distance on purchase.
        // The authored next_phase=-1 must not remove the shop.
        owner.ToNextPhase = false;
    }
}
