using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Details;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(3446u, 8265u, nameof(DoodadFuncRecoverItem), 15309u, SlaveKind.SmallSailingShip, false)]
    [Arguments(3446u, 8265u, nameof(DoodadFuncRecoverItem), 15309u, SlaveKind.Machine, true)]
    [Arguments(3446u, 8265u, nameof(DoodadFuncRecoverItem), 15309u, null, true)] // Housing storage.
    [Arguments(3446u, 8264u, nameof(DoodadFuncConsumeChanger), 15307u, SlaveKind.SmallSailingShip, false)]
    [Arguments(6560u, 17412u, nameof(DoodadFuncRecoverItem), 15309u, SlaveKind.Fishboat, true)]
    [Arguments(6655u, 17667u, nameof(DoodadFuncRecoverItem), 15309u, SlaveKind.Fishboat, true)]
    [Arguments(5200u, 13568u, nameof(DoodadFuncRecoverItem), 11361u, null, true)]
    [Arguments(2937u, 6619u, nameof(DoodadFuncLootItem), 0u, null, true)]
    public async Task DoodadCreation_AdvertisesTheStockAction(uint templateId, uint group, string function,
        uint skillId, SlaveKind? parentKind, bool loot)
    {
        var (doodad, world, _) = SetUpWorldForCargoPickup();
        SetField(DoodadManager.Instance, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [group] = [new() { GroupId = group, FuncType = function, SkillId = skillId }]
        });
        doodad.TemplateId = templateId;
        doodad.FuncGroupId = group;
        if (doodad.FuncGroupId == 8265)
            doodad.ItemId = 620;
        if (parentKind.HasValue)
        {
            var ship = new Slave { ObjId = 200, ParentWorld = world, Template = new SlaveTemplate { SlaveKind = parentKind.Value } };
            doodad.Transform.Parent = ship.Transform;
            doodad.ParentObjId = ship.ObjId;
            ship.AttachedDoodads.Add(doodad);
        }
        else if (templateId == 3446)
        {
            var house = new House { ObjId = 201, ParentWorld = world };
            doodad.Transform.Parent = house.Transform;
            doodad.ParentObjId = house.ObjId;
            doodad.OwnerType = DoodadOwnerType.Housing;
        }
        foreach (var batch in new[] { false, true })
            await Assert.That(ReadCreationAction(doodad, batch)).IsEqualTo((loot, group));
    }

    [Test]
    public async Task ShipRack_AfterOrdinaryStorage_LootOpenLeavesTheOriginalPackInPlace()
    {
        var (rack, world, live) = SetUpWorldForCargoPickup();
        var (ship, _) = SetUpShipRack(rack, world, live, 3446, 15309);
        var pack = PutOriginalPackOnRack(rack);
        _owner.Equipment = _owner.Inventory.Equipment;
        var manager = DoodadManager.Instance;
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [8265] = rack.CurrentFuncs,
            [8264] = [new() { FuncKey = 7481, GroupId = 8264, FuncId = 2, PermId = 5,
                FuncType = nameof(DoodadFuncConsumeChanger), SkillId = 15307, NextPhase = 8265 }]
        });
        SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncRecoverItem)] = new() { [393] = new DoodadFuncRecoverItem { Id = 393 } },
            [nameof(DoodadFuncConsumeChanger)] = new() { [2] = new DoodadFuncConsumeChanger { Id = 2, SlotId = 26, Count = 1 } }
        });
        SetField(manager, "_doodadFuncConsumeChangerItem", new Dictionary<uint, DoodadFuncConsumeChangerItem>
        { [1] = new() { Id = 1, DoodadFuncConsumeChangerId = 2, ItemId = pack.TemplateId } });
        _owner.Transform.Parent = ship.Transform;
        rack.Use(_owner, 15309);
        await Assert.That(_owner.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsSameReferenceAs(pack);
        await Assert.That(ReadCreationAction(rack, false)).IsEqualTo((false, 8264u));

        rack.Use(_owner, 15307);
        new CSLootOpenBagPacket { Connection = _owner.Connection }.Read(new PacketStream().WriteBc(rack.ObjId).WriteBc(0).Write(false));
        await Assert.That(_owner.SkillTask).IsNull();
        await Assert.That(_owner.Inventory.SystemContainer.Items.Single()).IsSameReferenceAs(pack);
        await Assert.That(_owner.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(rack.ItemId).IsEqualTo(pack.Id);
        await Assert.That(ReadCreationAction(rack, false)).IsEqualTo((false, 8265u));
    }

    private static (bool Loot, uint Group) ReadCreationAction(Doodad doodad, bool batch)
    {
        var data = batch
            ? new SCDoodadsCreatedPacket([doodad]).Write(new PacketStream())
            : new SCDoodadCreatedPacket(doodad).Write(new PacketStream());
        if (batch)
            data.ReadByte();
        data.ReadBc(); // object
        data.ReadUInt32(); // template
        data.ReadBc(); // owner
        data.ReadBc(); // parent
        data.ReadByte(); // socket
        data.ReadPosition();
        data.ReadInt16();
        data.ReadInt16();
        data.ReadInt16();
        data.ReadSingle(); // scale
        return (data.ReadBoolean(), data.ReadUInt32());
    }
}
