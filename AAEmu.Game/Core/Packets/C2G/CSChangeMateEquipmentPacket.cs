using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSChangeMateEquipmentPacket() : GamePacket(CSOffsets.CSChangeMateEquipmentPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        stream.ReadUInt32(); // Claimed owner. The authenticated character owns this operation.
        var mateTl = stream.ReadUInt16();
        stream.ReadUInt32(); // Passenger does not grant equipment access.
        var bts = stream.ReadBoolean();
        var count = stream.ReadByte();
        // r208022 397d13ca bounds the array to two equipment changes.
        if (count is 0 or > 2)
            return;

        var changes = new List<(ItemAndLocation Player, ItemAndLocation Mate)>();
        for (var i = 0; i < count; i++)
        {
            var player = new ItemAndLocation { Item = new EquipItem() };
            var mate = new ItemAndLocation { Item = new EquipItem() };
            player.Item.Read(stream);
            mate.Item.Read(stream);
            player.SlotType = (SlotType)stream.ReadByte();
            player.SlotNumber = stream.ReadByte();
            mate.SlotType = (SlotType)stream.ReadByte();
            mate.SlotNumber = stream.ReadByte();
            changes.Add((player, mate));
        }
        if (stream.LeftBytes != 0)
            return;

        var character = Connection.ActiveChar;
        if (character?.ParentWorld == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        {
            var manager = character.ParentWorld.MateManager;
            var mate = manager.GetOwnedMate(character, mateTl);
            if (mate == null)
                return;
            var bag = character.Inventory.Bag;
            var equipment = mate.Equipment;
            var checkedChanges = new List<(ItemAndLocation Player, ItemAndLocation Mate)>();
            var bagSlots = new HashSet<byte>();
            var mateSlots = new HashSet<byte>();
            // Native 397cdfc0 appends both original snapshots before 394e8dbb locks their slots.
            // Check the complete list before any inventory event or mutation.
            foreach (var change in changes)
            {
                var playerLocation = change.Player;
                var mateLocation = change.Mate;
                if (!manager.IsOwnedMate(character, mate))
                    return;
                var playerItem = bag.GetItemBySlot(playerLocation.SlotNumber);
                var mateItem = equipment.GetItemBySlot(mateLocation.SlotNumber);
                var valid = playerLocation.SlotType == SlotType.Inventory &&
                    mateLocation.SlotType == SlotType.EquipmentMate &&
                    playerLocation.SlotNumber < bag.ContainerSize && mateLocation.SlotNumber < equipment.ContainerSize &&
                    MatchesSnapshot(playerItem, playerLocation.Item) && MatchesSnapshot(mateItem, mateLocation.Item) &&
                    IsHeldOrEmpty(playerItem, bag) && IsHeldOrEmpty(mateItem, equipment) &&
                    TradeReservation.GetReservedCount(playerItem) == 0 && TradeReservation.GetReservedCount(mateItem) == 0 &&
                    bagSlots.Add(playerLocation.SlotNumber) && mateSlots.Add(mateLocation.SlotNumber) &&
                    bag.CanAccept(mateItem, playerLocation.SlotNumber) &&
                    equipment.CanAccept(playerItem, mateLocation.SlotNumber);
                var isEquip = playerItem != null;
                valid &= isEquip ? playerItem is EquipItem : mateItem is EquipItem;

                playerLocation.Item = playerItem;
                playerLocation.SlotType = SlotType.Inventory;
                mateLocation.Item = mateItem;
                mateLocation.SlotType = SlotType.EquipmentMate;
                if (!valid)
                {
                    character.SendPacket(new SCMateEquipmentChangedPacket(playerLocation, mateLocation,
                        mateTl, character.Id, 0, bts, false));
                    return;
                }

                checkedChanges.Add((playerLocation, mateLocation));
            }

            foreach (var (playerLocation, mateLocation) in checkedChanges)
            {
                if (!manager.IsOwnedMate(character, mate))
                    return;
                var isEquip = playerLocation.Item != null;
                var source = isEquip ? bag : equipment;
                var target = isEquip ? equipment : bag;
                var sourceItem = isEquip ? playerLocation : mateLocation;
                var destination = isEquip ? mateLocation : playerLocation;
                var success = character.Inventory.SplitOrMoveItemEx(ItemTaskType.Invalid, source, target,
                    sourceItem.Item.Id, source.ContainerType, sourceItem.SlotNumber,
                    0, target.ContainerType, destination.SlotNumber);
                if (!success)
                {
                    character.SendPacket(new SCMateEquipmentChangedPacket(playerLocation, mateLocation,
                        mateTl, character.Id, 0, bts, false));
                    return;
                }
            }
        }
    }

    private static bool IsHeldOrEmpty(Item item, ItemContainer container) =>
        item == null || item.Id != 0 && item.Count > 0 && ReferenceEquals(item._holdingContainer, container) &&
        item.OwnerId == container.OwnerId && item.SlotType == container.ContainerType &&
        container.Items.Count(candidate => candidate.Id == item.Id) == 1;

    internal static bool MatchesSnapshot(Item actual, Item claimed) =>
        actual == null ? claimed.TemplateId == 0 :
        actual.Id == claimed.Id && actual.TemplateId == claimed.TemplateId;
}
