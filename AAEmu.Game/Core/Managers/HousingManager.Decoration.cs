using System.Numerics;

using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers;

public partial class HousingManager
{
    internal Action<Doodad> PublishDecoration { get; set; } = doodad =>
    {
        doodad.InitDoodad();
        doodad.Spawn();
    };

    public bool DecorateHouse(Character player, ushort houseTlId, uint designId, Vector3 pos, Quaternion quat,
        uint parentObjId, ulong itemId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            return DecorateHouseLocked(player, houseTlId, designId, pos, quat, parentObjId, itemId);
        }
    }

    private bool DecorateHouseLocked(Character player, ushort houseTlId, uint designId, Vector3 pos,
        Quaternion quat, uint parentObjId, ulong itemId)
    {
        var bag = player?.Inventory?.Bag;
        var item = bag?.GetItemByItemId(itemId);
        var design = item == null ? null : HousingGameData.Instance.GetDecorationDesignForItem(item.TemplateId);
        if (item?.Template == null || item.OwnerId != player.Id || item._holdingContainer != bag ||
            item.SlotType != SlotType.Inventory || item.Count - TradeReservation.GetReservedCount(item) < 1 ||
            design == null || design.Id != designId)
            return false;

        var house = GetHouseByTlId(houseTlId);
        if (!IsActiveSaleHouse(house) || player.ParentWorld == null ||
            !ReferenceEquals(player.ParentWorld, house.ParentWorld))
        {
            player.SendErrorMessage(ErrorMessageType.InvalidHouseInfo);
            return false;
        }
        if (!house.AllowedToInteract(player))
        {
            player.SendErrorMessage(ErrorMessageType.InteractionPermissionDeny);
            return false;
        }
        if (!HousingAreaPolygon.IsFinite(pos) || !float.IsFinite(quat.LengthSquared()) || quat.LengthSquared() <= 0)
            return false;
        quat = Quaternion.Normalize(quat);

        var decorationItem = HousingGameData.Instance.GetItemHousingDecorationByItem(item.TemplateId);
        var houseDoodads = house.ParentWorld.GetDoodadByHouseDbId(house.Id)
            .Where(candidate => candidate.TemplateId != ForSaleMarkerDoodadId).ToArray();
        var limitError = HousingDecorationRules.Check(house.Template, house.CurrentStep, houseDoodads.Length,
            decorationItem, design, HousingDecorationRules.GetPlaced(houseDoodads, HousingGameData.Instance),
            HousingDecorationGameData.Instance);
        if (limitError != ErrorMessageType.NoErrorMessage)
        {
            player.SendErrorMessage(limitError);
            return false;
        }

        var geometryError = CheckDecorationGeometry(player, house, design, pos, quat, parentObjId);
        if (geometryError != ErrorMessageType.NoErrorMessage)
        {
            player.SendErrorMessage(geometryError);
            return false;
        }

        var skill = new Skill(new SkillTemplate())
        {
            CommitLaborBatch = (owner, write) =>
                (saveManager?.Value ?? SaveManager.Instance).TryCommitEconomy([owner], write)
        };
        return SkillLaborBatch.RunPlacement(player, skill, 0, () =>
        {
            var batch = SkillLaborBatch.Current;
            var prepared = item.Template.MaxCount > 1
                ? batch.Inventory.TryConsume(bag, item, 1)
                : item.Count == 1 && batch.Inventory.TryMove(item, player.Inventory.SystemContainer);
            if (!prepared)
            {
                batch.Fail();
                return;
            }

            var doodad = doodadManager.Create(house.ParentWorld, 0, design.DoodadId, house, true);
            if (doodad == null)
            {
                batch.Fail();
                return;
            }
            var ids = decorationIdManager ?? DoodadIdManager.Instance;
            batch.Enlist(null, () =>
            {
                doodad.Transform.Parent = null;
                objectIdManager.ReleaseId(doodad.ObjId);
                if (doodad.DbId > 0)
                    ids.ReleaseId(doodad.DbId);
            });
            doodad.DbId = ids.GetNextId();
            doodad.Transform.Parent = house.Transform;
            doodad.Transform.Local.SetPosition(pos.X, pos.Y, pos.Z);
            doodad.Transform.Local.ApplyFromQuaternion(quat);
            doodad.ItemTemplateId = item.TemplateId;
            doodad.ItemId = item.Template.MaxCount > 1 ? 0 : item.Id;
            doodad.OwnerDbId = house.Id;
            doodad.OwnerId = player.Id;
            doodad.ParentObjId = house.ObjId;
            doodad.ParentObj = house;
            doodad.AttachPoint = AttachPointKind.None;
            doodad.OwnerType = DoodadOwnerType.Housing;
            doodad.UccId = uccManager.GetUccFromItem(item)?.Id ?? 0;
            if (item is BigFish fish)
                doodad.SetData(((short)fish.Length << 16) + (short)fish.Weight);
            doodad.IsPersistent = true;
            if (doodad is DoodadCoffer coffer)
            {
                coffer.InitializeCoffer(player.Id);
                batch.Enlist(null, () => ItemManager.Instance.ForgetCommittedItemContainer(coffer.ItemContainer));
            }
            doodad.InitializeGrowthTime();
            if (doodad.PhaseTime == DateTime.MinValue)
                doodad.PhaseTime = doodad.PlantTime;
            batch.TrackDoodad(doodad);
            batch.AfterCommit(() => PublishDecoration(doodad));
        });
    }
}
