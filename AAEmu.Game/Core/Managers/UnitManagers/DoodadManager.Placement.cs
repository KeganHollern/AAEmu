using System.Numerics;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers.UnitManagers;

public partial class DoodadManager
{
    internal Func<Character, Action<PersistenceSaveContext>, bool> CommitPlayerDoodadPlacement { get; set; } =
        (character, write) => SaveManager.Instance.TryCommitEconomy([character], write);
    internal Action<Doodad> PublishPlayerDoodadPlacement { get; set; } = doodad =>
    {
        doodad.InitDoodad();
        doodad.Spawn();
        doodad.ParentWorld.SpawnManager.AddPlayerDoodad(doodad);
    };

    private Doodad CreatePaidPlayerDoodad(Character character, uint templateId, float x, float y, float z,
        float zRot, float scale, ulong itemId, FarmType farmType, int customData, bool ignoreHouses, int laborCost)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var bag = character?.Inventory?.Bag;
            var item = bag?.GetItemByItemId(itemId);
            if (item?.Template == null || item.OwnerId != character.Id || item._holdingContainer != bag ||
                item.SlotType != SlotType.Inventory || item.Count - TradeReservation.GetReservedCount(item) < 1 ||
                templateId == 0 || itemManager.GetDoodadTemplateIdForItem(item.TemplateId) != templateId ||
                !_templates.TryGetValue(templateId, out var template) || character.ParentWorld == null ||
                !ZoneSkillRestrictions.CanUseItem(character, item, new Vector3(x, y, z)))
                return null;

            var position = new Vector3(x, y, z);
            var world = character.ParentWorld.Template;
            // Reject an unknown or out-of-world destination before indexing the zone map.
            if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 ||
                world.ZoneKeyByRegions == null ||
                x >= world.ZoneKeyByRegions.GetLength(0) * WorldManager.REGION_SIZE ||
                y >= world.ZoneKeyByRegions.GetLength(1) * WorldManager.REGION_SIZE)
                return null;
            var zoneKey = WorldManager.Instance.GetZoneId(world, x, y);
            var zoneGroup = ZoneManager.Instance.GetZoneByKey(zoneKey)?.GroupId ?? 0;
            if (!PlayerDoodadPlacementRules.Check(character.Transform.World.Position, position, zRot, scale,
                    template.RestrictZoneId, zoneGroup) ||
                (template.RestrictZoneId != 0 &&
                 ZoneManager.Instance.GetZoneByKey(character.Transform.ZoneId)?.GroupId != template.RestrictZoneId))
                return null;

            var targetHouse = ignoreHouses ? null : housingManager.Value.GetHouseAtLocation(character.ParentWorld, new Vector3(x, y, z));
            if (targetHouse != null && !targetHouse.AllowedToInteract(character))
                return null;
            var useSkill = SkillManager.Instance.GetSkillTemplate(item.Template.UseSkillId);
            var skill = new Skill(useSkill ?? new SkillTemplate()) { CommitLaborBatch = CommitPlayerDoodadPlacement };
            Doodad doodad = null;
            var succeeded = SkillLaborBatch.RunPlacement(character, skill, laborCost, () =>
            {
                var batch = SkillLaborBatch.Current;
                var preparedItem = item.Template.MaxCount > 1
                    ? batch.Inventory.TryConsume(bag, item, 1)
                    : item.Count == 1 && batch.Inventory.TryMove(item, character.Inventory.SystemContainer);
                if (!preparedItem)
                {
                    batch.Fail();
                    return;
                }

                doodad = Create(character.ParentWorld, 0, templateId, character, true);
                if (doodad == null)
                {
                    batch.Fail();
                    return;
                }
                batch.Enlist(null, () =>
                {
                    doodad.Transform.Parent = null;
                    objectIdManager.ReleaseId(doodad.ObjId);
                    if (doodad.DbId > 0)
                        doodadIdManager.ReleaseId(doodad.DbId);
                });
                doodad.DbId = doodadIdManager.GetNextId();
                doodad.IsPersistent = true;
                doodad.Transform = character.Transform.CloneDetached(doodad);
                doodad.Transform.Local.SetPosition(x, y, z);
                doodad.Transform.Local.SetRotation(0, 0, zRot);
                doodad.ItemId = item.Template.MaxCount > 1 ? 0 : item.Id;
                doodad.ItemTemplateId = item.TemplateId;
                doodad.UccId = item.UccId;
                doodad.FarmType = farmType;
                doodad.SetData(customData);
                if (targetHouse != null)
                {
                    doodad.OwnerDbId = targetHouse.Id;
                    doodad.AttachPoint = AttachPointKind.None;
                    doodad.OwnerType = DoodadOwnerType.Housing;
                    doodad.ParentObj = targetHouse;
                    doodad.ParentObjId = targetHouse.ObjId;
                    doodad.Transform.Parent = targetHouse.Transform;
                }
                if (scale > 0f)
                    doodad.SetScale(scale);
                if (doodad is DoodadCoffer coffer)
                {
                    coffer.InitializeCoffer(character.Id);
                    batch.Enlist(null, () => ItemManager.Instance.ForgetCommittedItemContainer(coffer.ItemContainer));
                }

                doodad.InitializeGrowthTime();
                if (doodad.PhaseTime == DateTime.MinValue)
                    doodad.PhaseTime = doodad.PlantTime;
                batch.TrackDoodad(doodad);
                batch.AfterCommit(() =>
                {
                    PublishPlayerDoodadPlacement(doodad);
                    character.ItemUse(item);
                });
            });
            return succeeded ? doodad : null;
        }
    }
}
