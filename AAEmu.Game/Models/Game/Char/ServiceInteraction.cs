using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.Char;

internal static class ServiceInteraction
{
    private const float MerchantRange = 3f;
    private const uint OpenWarehouseSkill = 13238;

    internal static bool CanReach(Character character, BaseUnit target, float range = 5f) =>
        IsLiveServiceTarget(character, target) && CenterDistance(character, target) <= range;

    internal static bool CanReachMerchant(Character character, Npc npc)
    {
        if (npc?.Template?.Merchant != true || !IsLiveServiceTarget(character, npc))
            return false;

        if (character.ModelId != 0 && npc.ModelId != 0 &&
            ModelManager.Instance.GetActorModel(character.ModelId) is { } characterModel &&
            ModelManager.Instance.GetActorModel(npc.ModelId) is { } merchantModel)
            return QuestInteraction.GetActorEdgeDistance(character, characterModel, npc, merchantModel) <= MerchantRange;

        return CenterDistance(character, npc) <= MerchantRange;
    }

    private static float CenterDistance(Character character, BaseUnit target) =>
        MathUtil.CalculateDistance(character.Transform.World.Position, target.Transform.World.Position, true);

    private static bool IsLiveServiceTarget(Character character, BaseUnit target)
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
        return ReferenceEquals(current, target);
    }

    internal static bool CanUseNpc(Character character, Npc npc, Func<NpcTemplate, bool> supports, float range = 5f) =>
        npc?.Template != null && supports(npc.Template) && ReferenceEquals(character?.CurrentInteractionObject, npc) &&
        CanReach(character, npc, range);

    internal static bool CanUseDoodad(Character character, string function, uint objectId = 0) =>
        character?.CurrentInteractionObject is Doodad doodad && (objectId == 0 || doodad.ObjId == objectId) &&
        doodad.CurrentFuncs?.Any(func => func.FuncType == function) == true && CanReach(character, doodad);

    internal static bool CanUseBank(Character character)
    {
        if (CanUseNpc(character, character?.CurrentInteractionObject as Npc, npc => npc.Banker) ||
            CanUseDoodad(character, nameof(DoodadFuncBankUi)))
            return true;

        // The stock client opens a fixed Warehouse locally, without binding the Game interaction target.
        return WorldManager.GetAround<Doodad>(character).Any(warehouse =>
            warehouse.OwnerType == DoodadOwnerType.System &&
            warehouse.Despawn == DateTime.MinValue &&
            warehouse.ParentWorld?.SpawnManager?.IsAuthoredDoodadSpawner(warehouse.Spawner) == true &&
            warehouse.CurrentFuncs?.Any(action => action.SkillId == OpenWarehouseSkill &&
                action.FuncType == nameof(DoodadFuncBankUi) &&
                DoodadPermissionRules.Allows(character, warehouse, action.PermId)) == true &&
            SkillManager.Instance.GetSkillTemplate(OpenWarehouseSkill) is { MaxRange: > 0 } skill &&
            CanReach(character, warehouse, skill.MaxRange));
    }

    internal static bool CanUseAuction(Character character) =>
        CanUseNpc(character, character?.CurrentInteractionObject as Npc, npc => npc.Auctioneer) ||
        CanUseDoodad(character, nameof(DoodadObj.Funcs.DoodadFuncAuctionUi));

    internal static bool CanUseMailbox(Character character, uint objectId = 0) =>
        CanUseDoodad(character, nameof(DoodadFuncNaviOpenMailbox), objectId);

    internal static bool CanSendMail(Character character, uint mailboxId) =>
        mailboxId != 0 && character?.ParentWorld?.GetDoodad(mailboxId) is { } mailbox &&
        (ReferenceEquals(character.CurrentInteractionObject, mailbox) || IsAuthoredFixedMailbox(mailbox)) &&
        IsUsableMailbox(character, mailbox);

    internal static bool CanClaimMail(Character character)
    {
        if (CanUseMailbox(character))
            return true;

        // r208022 keeps the mailbox target and cached inbox on the client. A
        // claim can be the first packet after it reopens a fixed mailbox.
        return WorldManager.GetAround<Doodad>(character)
            .Any(mailbox => IsAuthoredFixedMailbox(mailbox) && IsUsableMailbox(character, mailbox));
    }

    private static bool IsAuthoredFixedMailbox(Doodad mailbox) =>
        mailbox.ParentWorld?.SpawnManager?.IsAuthoredDoodadSpawner(mailbox.Spawner) == true;

    private static bool IsUsableMailbox(Character character, Doodad mailbox) =>
        mailbox.Despawn == DateTime.MinValue && CanReach(character, mailbox) &&
        mailbox.CurrentFuncs?.Any(func => func.FuncType == nameof(DoodadFuncNaviOpenMailbox) &&
            DoodadPermissionRules.Allows(character, mailbox, func.PermId)) == true;
}
