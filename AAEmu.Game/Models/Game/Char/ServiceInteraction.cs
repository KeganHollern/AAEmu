using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.Char;

internal static class ServiceInteraction
{
    internal static bool CanReach(Character character, BaseUnit target, float range = 5f)
    {
        var world = character?.ParentWorld;
        if (world == null || target?.Transform == null || character.Transform == null || target.ObjId == 0 ||
            !ReferenceEquals(world, target.ParentWorld) ||
            character.Transform.InstanceId != target.Transform.InstanceId ||
            character.Transform.WorldId != target.Transform.WorldId)
            return false;
        var current = target switch
        {
            Npc npc when !npc.Despawned => world.GetNpc(target.ObjId),
            Doodad => (BaseUnit)world.GetDoodad(target.ObjId),
            _ => null
        };
        return ReferenceEquals(current, target) &&
            MathUtil.CalculateDistance(character.Transform.World.Position, target.Transform.World.Position, true) <= range;
    }

    internal static bool CanUseNpc(Character character, Npc npc, Func<NpcTemplate, bool> supports, float range = 5f) =>
        npc?.Template != null && supports(npc.Template) && ReferenceEquals(character?.CurrentInteractionObject, npc) &&
        CanReach(character, npc, range);

    internal static bool CanUseDoodad(Character character, string function, uint objectId = 0) =>
        character?.CurrentInteractionObject is Doodad doodad && (objectId == 0 || doodad.ObjId == objectId) &&
        doodad.CurrentFuncs?.Any(func => func.FuncType == function) == true && CanReach(character, doodad);

    internal static bool CanUseBank(Character character) =>
        CanUseNpc(character, character?.CurrentInteractionObject as Npc, npc => npc.Banker) ||
        CanUseDoodad(character, nameof(DoodadObj.Funcs.DoodadFuncBankUi));

    internal static bool CanUseAuction(Character character) =>
        CanUseNpc(character, character?.CurrentInteractionObject as Npc, npc => npc.Auctioneer) ||
        CanUseDoodad(character, nameof(DoodadObj.Funcs.DoodadFuncAuctionUi));

    internal static bool CanUseMailbox(Character character, uint objectId = 0) =>
        CanUseDoodad(character, nameof(DoodadObj.Funcs.DoodadFuncNaviOpenMailbox), objectId);
}
