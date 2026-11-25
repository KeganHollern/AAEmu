using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>Prepares furniture ownership beside the house, items and mail transaction.</summary>
internal sealed class HousingFurnitureSettlement(House house, IItemManager items) : IDisposable
{
    private readonly Dictionary<Doodad, DoodadState> _states = [];
    private readonly HashSet<Doodad> _removed = [];
    private readonly Dictionary<uint, List<Item>> _returned = [];
    private bool _finished;
    private bool _demolition;
    private bool _removeSavedHouse;

    public IReadOnlyDictionary<uint, List<Item>> ReturnedItems => _returned;

    public bool TryPrepare(Character buyer, InventoryMutation inventory) =>
        Prepare(buyer, inventory, false, false);

    public bool TryPrepareDemolition(InventoryMutation inventory, bool forceRestoreAllDecor, bool removeSavedHouse) =>
        Prepare(null, inventory, forceRestoreAllDecor, removeSavedHouse);

    private bool Prepare(Character buyer, InventoryMutation inventory, bool forceRestoreAllDecor, bool removeSavedHouse)
    {
        _demolition = buyer == null;
        _removeSavedHouse = removeSavedHouse;
        var furniture = (house.ParentWorld?.GetDoodadByHouseDbId(house.Id) ?? [])
            .Where(d => d.OwnerType == DoodadOwnerType.Housing)
            .Concat(house.AttachedDoodads).Distinct().Where(d =>
                (d.TemplateId != HousingManager.ForSaleMarkerDoodadId || _demolition) &&
                (!_demolition || removeSavedHouse || d.AttachPoint == AttachPointKind.None || d is DoodadCoffer)).ToArray();
        var replacementCounts = new Dictionary<(uint Owner, uint Template), int>();
        foreach (var doodad in furniture)
            _states.Add(doodad, new DoodadState(doodad));

        foreach (var doodad in furniture)
        {
            if (doodad.Despawn > DateTime.MinValue || (doodad.IsPersistent && doodad.DbId == 0))
                return false;
            if (doodad.AttachPoint != AttachPointKind.None && doodad is not DoodadCoffer)
            {
                if (!_demolition)
                    TransferOwner(doodad, buyer);
                else
                {
                    // Keep the ordinary door visible on the voluntary ruin until it leaves the world.
                    if (doodad.ItemId != 0)
                        return false;
                    doodad.IsPersistent = false;
                }
                continue;
            }
            if (_demolition && doodad.TemplateId == HousingManager.ForSaleMarkerDoodadId)
            {
                if (doodad.ItemId != 0 || doodad is DoodadCoffer)
                    return false;
                StageRemoval(doodad);
                continue;
            }
            var design = HousingGameData.Instance.GetDecorationDesignFromDoodadId(doodad.TemplateId);
            var decoration = design == null ? null : HousingGameData.Instance.GetItemHousingDecorations(design.Id);
            if (_demolition && doodad is DoodadCoffer fixture && decoration == null)
            {
                // A fixed coffer can have contents without a portable furniture design.
                // A backed coffer needs a known return rule before its backing can move.
                if (fixture.ItemId != 0 || !EmptyCoffer(fixture, inventory, house.OwnerId))
                    return false;
                if (fixture.AttachPoint != AttachPointKind.None)
                {
                    StageRemoval(fixture);
                    continue;
                }
            }
            if (decoration == null)
            {
                doodad.Transform.Parent = null;
                doodad.ParentObj = null;
                doodad.ParentObjId = 0;
                doodad.OwnerDbId = 0;
                doodad.OwnerType = DoodadOwnerType.Character;
                continue;
            }

            var backingItem = doodad.ItemId == 0 ? null : items.GetItemByItemId(doodad.ItemId);
            if (doodad.ItemId > 0 && (backingItem == null || backingItem.Count != 1 ||
                backingItem.OwnerId == 0 || backingItem.SlotType != SlotType.System ||
                backingItem._holdingContainer == null))
                return false;

            if (doodad is DoodadCoffer coffer)
            {
                if (!EmptyCoffer(coffer, inventory, _demolition ? house.OwnerId : null))
                    return false;
                if (!_demolition)
                    coffer.ItemContainer.SetOwnerId(buyer.Id);
            }

            var bound = backingItem?.ItemFlags.HasFlag(ItemFlag.SoulBound) ??
                (items.GetTemplate(doodad.ItemTemplateId)?.BindType is ItemBindType.BindOnPickup or ItemBindType.BindOnPickupPack);
            if (bound || _demolition && (decoration.Restore || forceRestoreAllDecor))
            {
                if (backingItem != null)
                {
                    if (!ReturnItem(backingItem, inventory, _demolition ? house.OwnerId : null))
                        return false;
                }
                else
                {
                    var recipient = _demolition ? house.OwnerId : doodad.OwnerId;
                    if (recipient == 0 || doodad.ItemTemplateId == 0)
                        return false;
                    if (_demolition)
                    {
                        var key = (recipient, doodad.ItemTemplateId);
                        replacementCounts[key] = replacementCounts.GetValueOrDefault(key) + 1;
                    }
                    else if (!GrantReplacement(inventory, recipient, doodad.ItemTemplateId, 1))
                        return false;
                }
                StageRemoval(doodad);
            }
            else if (_demolition)
            {
                if (backingItem != null && !inventory.TryConsume(backingItem._holdingContainer, backingItem, 1))
                    return false;
                StageRemoval(doodad);
            }
            else
            {
                if (backingItem != null && backingItem._holdingContainer != buyer.Inventory.SystemContainer &&
                    !inventory.TryMove(backingItem, buyer.Inventory.SystemContainer))
                    return false;
                TransferOwner(doodad, buyer);
            }
        }

        foreach (var ((owner, template), count) in replacementCounts)
            if (!GrantReplacement(inventory, owner, template, count))
                return false;

        // Children of returned decorations retain their world position on the property.
        foreach (var doodad in furniture.Where(d => !_removed.Contains(d)))
        {
            if (doodad.Transform.Parent?.GameObject is Doodad parent && _removed.Contains(parent))
            {
                doodad.Transform.Parent = doodad.OwnerDbId == house.Id ? house.Transform : null;
                doodad.ParentObj = doodad.Transform.Parent?.GameObject;
                doodad.ParentObjId = doodad.ParentObj?.ObjId ?? 0;
            }
        }
        return true;
    }

    private bool EmptyCoffer(DoodadCoffer coffer, InventoryMutation inventory, uint? demolitionOwner)
    {
        if (coffer.ItemContainer == null || coffer.ItemContainer.ContainerId == 0)
            return false;
        foreach (var item in coffer.ItemContainer.Items.ToArray())
            if (!ReturnItem(item, inventory, demolitionOwner))
                return false;
        coffer.OpenedBy = null;
        return true;
    }

    private bool GrantReplacement(InventoryMutation inventory, uint owner, uint template, int count)
    {
        var destination = items.GetItemContainerForCharacter(owner, SlotType.Mail, null, 0);
        if (!inventory.TryGrant(destination, template, count, out var granted))
            return false;
        ReturnedFor(owner).AddRange(granted);
        return true;
    }

    private void StageRemoval(Doodad doodad)
    {
        _removed.Add(doodad);
        doodad.ItemId = 0;
        doodad.Despawn = DateTime.UtcNow;
        // Prevent an independent doodad save from recreating a committed deletion.
        doodad.IsPersistent = false;
    }

    private bool ReturnItem(Item item, InventoryMutation inventory, uint? demolitionOwner)
    {
        if (item.OwnerId is 0 or > uint.MaxValue)
            return false;
        var owner = demolitionOwner ?? (uint)item.OwnerId;
        var destination = items.GetItemContainerForCharacter(owner, SlotType.Mail, null, 0);
        if (!inventory.TryMove(item, destination))
            return false;
        ReturnedFor(owner).Add(item);
        return true;
    }

    private List<Item> ReturnedFor(uint owner)
    {
        if (!_returned.TryGetValue(owner, out var result))
            _returned.Add(owner, result = []);
        return result;
    }

    private static void TransferOwner(Doodad doodad, Character buyer)
    {
        doodad.OwnerId = buyer.Id;
        doodad.OwnerObjId = buyer.ObjId;
        doodad.Faction = buyer.Faction;
    }

    public void Save(PersistenceSaveContext context)
    {
        foreach (var (doodad, original) in _states)
        {
            if (original.Persistent)
            {
                using var command = context.Connection.CreateCommand();
                command.Transaction = context.Transaction;
                command.Parameters.AddWithValue("@id", doodad.DbId);
                if (!doodad.IsPersistent)
                    command.CommandText = "DELETE FROM doodads WHERE id=@id";
                else
                {
                    command.CommandText = "UPDATE doodads SET owner_id=@owner,owner_type=@type,house_id=@house," +
                        "parent_doodad=@parent,x=@x,y=@y,z=@z,roll=@roll,pitch=@pitch,yaw=@yaw,item_id=@item WHERE id=@id";
                    command.Parameters.AddWithValue("@owner", doodad.OwnerId);
                    command.Parameters.AddWithValue("@type", doodad.OwnerType);
                    command.Parameters.AddWithValue("@house", doodad.OwnerDbId);
                    command.Parameters.AddWithValue("@parent", (doodad.Transform.Parent?.GameObject as Doodad)?.DbId ?? 0);
                    command.Parameters.AddWithValue("@x", doodad.Transform.Local.Position.X);
                    command.Parameters.AddWithValue("@y", doodad.Transform.Local.Position.Y);
                    command.Parameters.AddWithValue("@z", doodad.Transform.Local.Position.Z);
                    command.Parameters.AddWithValue("@roll", doodad.Transform.Local.Rotation.X);
                    command.Parameters.AddWithValue("@pitch", doodad.Transform.Local.Rotation.Y);
                    command.Parameters.AddWithValue("@yaw", doodad.Transform.Local.Rotation.Z);
                    command.Parameters.AddWithValue("@item", doodad.ItemId);
                }
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Furniture row {doodad.DbId} did not settle.");
            }
            if (_removed.Contains(doodad) && doodad is DoodadCoffer coffer)
            {
                using var containerCommand = context.Connection.CreateCommand();
                containerCommand.Transaction = context.Transaction;
                containerCommand.CommandText = "DELETE FROM item_containers WHERE container_id=@id " +
                    "AND NOT EXISTS (SELECT 1 FROM items WHERE container_id=@id)";
                containerCommand.Parameters.AddWithValue("@id", coffer.ItemContainer.ContainerId);
                if (containerCommand.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The returned coffer container did not settle.");
                context.AfterCommit(() => ItemManager.Instance.ForgetCommittedItemContainer(coffer.ItemContainer));
            }
        }
        if (_demolition)
        {
            // An unloaded furniture or coffer row must keep the former owner and all returns intact.
            using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = "SELECT id FROM doodads WHERE house_id=@id AND owner_type=@housing" +
                (_removeSavedHouse ? "" : " AND (COALESCE(attach_point,0)=0 OR item_container_id<>0)") + " LIMIT 1";
            command.Parameters.AddWithValue("@id", house.Id);
            command.Parameters.AddWithValue("@housing", DoodadOwnerType.Housing);
            if (command.ExecuteScalar() != null)
                throw new InvalidOperationException($"Housing {house.Id} has unsettled furniture.");
        }
    }

    public void Complete()
    {
        _finished = true;
        foreach (var (doodad, original) in _states)
        {
            if (doodad is DoodadCoffer coffer && original.OpenedBy != null)
                for (var slot = 0; slot < coffer.Capacity; slot += SCCofferContentsUpdatePacket.MaxSlotsToSend)
                    original.OpenedBy.SendPacket(new SCCofferContentsUpdatePacket(coffer, (byte)slot));
            if (_removed.Contains(doodad))
            {
                doodad.Transform.DetachAll();
                house.AttachedDoodads.Remove(doodad);
                house.ParentWorld?.SpawnManager.RemovePlayerDoodad(doodad);
                doodad.Delete();
            }
            else
            {
                if (doodad.OwnerDbId != house.Id)
                    house.AttachedDoodads.Remove(doodad);
                else if (_removeSavedHouse && doodad.AttachPoint != AttachPointKind.None)
                {
                    house.ParentWorld?.SpawnManager.RemovePlayerDoodad(doodad);
                    if (!house.AttachedDoodads.Contains(doodad))
                        house.AttachedDoodads.Add(doodad);
                }
                doodad.BroadcastPacket(new SCDoodadOriginatorPacket(doodad.ObjId, doodad.OwnerId, doodad.Faction?.Id ?? 0), true);
            }
        }
    }

    public void PreservePreparedState() => _finished = true;

    public void Dispose()
    {
        if (_finished)
            return;
        _finished = true;
        // Rebuild links before restoring local positions, so parent conversion cannot alter the snapshot.
        foreach (var (doodad, state) in _states)
            doodad.Transform.Parent = state.Parent;
        foreach (var (doodad, state) in _states)
            state.Restore(doodad);
    }

    private sealed class DoodadState(Doodad doodad)
    {
        private readonly uint _owner = doodad.OwnerId, _ownerObj = doodad.OwnerObjId,
            _house = doodad.OwnerDbId, _parentObjId = doodad.ParentObjId;
        private readonly DoodadOwnerType _type = doodad.OwnerType;
        private readonly ulong _item = doodad.ItemId;
        private readonly DateTime _despawn = doodad.Despawn;
        private readonly SystemFaction _faction = doodad.Faction;
        private readonly GameObject _parentObj = doodad.ParentObj;
        private readonly Vector3 _position = doodad.Transform.Local.Position, _rotation = doodad.Transform.Local.Rotation;
        private readonly uint _cofferOwner = (doodad as DoodadCoffer)?.ItemContainer?.OwnerId ?? 0;
        private readonly bool _cofferDirty = (doodad as DoodadCoffer)?.ItemContainer?.IsDirty ?? false;
        public Transform Parent { get; } = doodad.Transform.Parent;
        public bool Persistent { get; } = doodad.IsPersistent;
        public Character OpenedBy { get; } = (doodad as DoodadCoffer)?.OpenedBy;

        public void Restore(Doodad doodad)
        {
            doodad.OwnerId = _owner;
            doodad.OwnerObjId = _ownerObj;
            doodad.OwnerDbId = _house;
            doodad.OwnerType = _type;
            doodad.ParentObjId = _parentObjId;
            doodad.ParentObj = _parentObj;
            doodad.ItemId = _item;
            doodad.IsPersistent = Persistent;
            doodad.Despawn = _despawn;
            doodad.Faction = _faction;
            doodad.Transform.Local.Position = _position;
            doodad.Transform.Local.Rotation = _rotation;
            if (doodad is DoodadCoffer { ItemContainer: not null } coffer)
            {
                coffer.ItemContainer.SetOwnerId(_cofferOwner);
                coffer.ItemContainer.IsDirty = _cofferDirty;
                coffer.OpenedBy = OpenedBy;
            }
        }
    }
}
