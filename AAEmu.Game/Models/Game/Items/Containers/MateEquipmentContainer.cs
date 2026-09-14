using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Items.Containers;

public class MateEquipmentContainer : EquipmentContainer
{
    public MateEquipmentContainer(uint ownerId, SlotType containerType, bool createWithNewId, Unit parentUnit) : base(ownerId, containerType, createWithNewId, parentUnit)
    {
        // Fancy way of getting the last enum value + 1 for equipment slots
        ContainerSize = (int)Enum.GetValues<EquipmentItemSlot>().Max() + 1;
    }

    public override bool CanAccept(Item item, int targetSlot)
    {
        if (targetSlot < 0 || targetSlot >= ContainerSize)
            return false;
        if (item == null)
            return true;
        if (ParentUnit is not Units.Mate mate || mate.Template == null ||
            !MateGameData.Instance.HasEquipmentSlot(mate.Template.MateEquipSlotPackId, targetSlot))
            return false;
        // r208022 uses item tags 29 (mate equipment) and 1259 (underwater mate equipment).
        var tags = TagsGameData.Instance;
        if (!tags.GetIdsByTagId(TagsGameData.TagType.Items, 29).Contains(item.TemplateId) ||
            tags.GetIdsByTagId(TagsGameData.TagType.Items, 1259).Contains(item.TemplateId) !=
            MateGameData.Instance.IsUnderwaterModel(mate.ModelId))
            return false;
        return item is EquipItem && item.Template is ArmorTemplate armor &&
            mate.Level >= armor.LevelRequirement && (armor.LevelLimit == 0 || mate.Level <= armor.LevelLimit) &&
            GetAllowedGearSlots(armor).Contains((EquipmentItemSlot)targetSlot);
    }

    public override void OnEnterContainer(Item item, ItemContainer lastContainer, byte previousSlot)
    {
        base.OnEnterContainer(item, lastContainer, previousSlot); // base EquipmentContainer

        // Extra pockets for mates
        if (ParentUnit is not Units.Mate mate)
        {
            return;
        }

        var petItem = new ItemAndLocation
        {
            Item = item,
            SlotType = lastContainer.ContainerType, // ContainerType,
            SlotNumber = previousSlot,
        };
        var inventoryItem = new ItemAndLocation
        {
            Item = null,
            SlotType = ContainerType,
            SlotNumber = (byte)item.Slot,
        };
        // Owner.SendMessage($"MateEquipmentContainer - {petItem} -> {inventoryItem}, MateTl: {mate.TlId}");
        Owner.SendPacket(new SCMateEquipmentChangedPacket(petItem, inventoryItem, mate.TlId, Owner.Id, 0, false, true));
    }

    public override void OnLeaveContainer(Item item, ItemContainer newContainer, byte previousSlot)
    {
        base.OnLeaveContainer(item, newContainer, previousSlot); // base EquipmentContainer

        // Extra pockets for mates
        if (ParentUnit is not Units.Mate mate)
        {
            return;
        }

        var petItem = new ItemAndLocation
        {
            Item = null,
            SlotType = item.SlotType, // newContainer
            SlotNumber = (byte)item.Slot,
        };
        var inventoryItem = new ItemAndLocation
        {
            Item = item,
            SlotType = ContainerType,
            SlotNumber = previousSlot,
        };
        // Owner.SendMessage($"MateEquipmentContainer - {petItem} -> {inventoryItem}, MateTl: {mate.TlId}");
        Owner.SendPacket(new SCMateEquipmentChangedPacket(petItem, inventoryItem, mate.TlId, Owner.Id, 0, false, true));
    }
}
