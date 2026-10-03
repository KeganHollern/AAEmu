using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments("combat")]
    [Arguments("visibility")]
    [Arguments("cargo")]
    public async Task VehicleReplacement_RejectionCancelsChildAndParentBatchWithoutCost(string rejection)
    {
        var (world, slave, replacementItem) = PrepareVehiclePlacementBoundary();
        var placementChecks = 0;
        world.SlaveManager.PlacementGeometry = (_, _, _) =>
        {
            placementChecks++;
            return ErrorMessageType.NoErrorMessage;
        };
        slave.IsVisible = rejection != "visibility";
        if (rejection == "combat")
            slave.IsInBattle = true;
        if (rejection == "cargo")
            slave.AttachedDoodads.Add(new Doodad { ItemId = 42 });
        var parentSkill = NewSkill();
        var childSkill = new Skill(new SkillTemplate { Id = 777 });
        var committed = false;
        parentSkill.CommitLaborBatch = (_, _) => { committed = true; return true; };
        var completionEffect = false;

        var accepted = SkillLaborBatch.Run(_owner, parentSkill, true, () =>
        {
            SkillLaborBatch.Current.Consume(_owner.Inventory.Bag, _material.TemplateId, 1, _material);
            SkillLaborBatch.Current.AfterCommit(() => completionEffect = true);
            new SpawnSlave().Execute(_owner, new SkillItem { ItemId = replacementItem.Id }, _owner, ValidVehicleTarget(),
                null, childSkill, null, DateTime.UtcNow, 0, 0, 0, 0);
        });

        await Assert.That(accepted).IsFalse();
        await Assert.That(childSkill.Cancelled).IsTrue();
        await Assert.That(placementChecks).IsEqualTo(1);
        await Assert.That(parentSkill.Cancelled).IsTrue();
        await Assert.That(committed || completionEffect).IsFalse();
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(world.GetAllSlaves().Single()).IsSameReferenceAs(slave);
        await Assert.That(slave.AttachmentsRetired).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task VehicleReplacement_DamagedItemRejectsBeforeRemovingCurrentVehicleOrChargingCosts(bool recovering)
    {
        var (world, currentVehicle, replacementItem) = PrepareVehiclePlacementBoundary();
        var placementChecks = 0;
        world.SlaveManager.PlacementGeometry = (_, _, _) =>
        {
            placementChecks++;
            return ErrorMessageType.NoErrorMessage;
        };
        var repairStart = recovering ? DateTime.UtcNow.AddMinutes(-5) : DateTime.MinValue;
        replacementItem.RepairStartTime = repairStart;
        replacementItem.IsDestroyed = recovering ? (byte)0 : (byte)1;
        var parentSkill = NewSkill();
        var childSkill = new Skill(new SkillTemplate { Id = 777 });
        var committed = false;
        parentSkill.CommitLaborBatch = (_, _) => { committed = true; return true; };
        var completionEffect = false;

        var accepted = SkillLaborBatch.Run(_owner, parentSkill, true, () =>
        {
            SkillLaborBatch.Current.Consume(_owner.Inventory.Bag, _material.TemplateId, 1, _material);
            SkillLaborBatch.Current.AfterCommit(() => completionEffect = true);
            new SpawnSlave().Execute(_owner, new SkillItem { ItemId = replacementItem.Id }, _owner, ValidVehicleTarget(),
                null, childSkill, null, DateTime.UtcNow, 0, 0, 0, 0);
        });

        await Assert.That(accepted).IsFalse();
        await Assert.That(childSkill.Cancelled && parentSkill.Cancelled).IsTrue();
        await Assert.That(placementChecks).IsEqualTo(1);
        await Assert.That(committed || completionEffect).IsFalse();
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Inventory.GetItemById(replacementItem.Id)).IsSameReferenceAs(replacementItem);
        await Assert.That(replacementItem.Count).IsEqualTo(1);
        await Assert.That(replacementItem.SlaveDbId).IsEqualTo(77u);
        await Assert.That(replacementItem.IsDestroyed).IsEqualTo(recovering ? (byte)0 : (byte)1);
        await Assert.That(replacementItem.RepairStartTime).IsEqualTo(repairStart);
        await Assert.That(world.GetAllSlaves().Single()).IsSameReferenceAs(currentVehicle);
        await Assert.That(currentVehicle.AttachmentsRetired).IsFalse();
        await Assert.That(currentVehicle.Despawn).IsEqualTo(DateTime.MinValue);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task VehiclePlacement_MissingOrWrongTargetRejectsBeforeReplacementAndCostCommit(bool wrongTarget) =>
        AssertVehiclePlacementRejected(wrongTarget ? "unit" : "missing");

    [Test]
    [Arguments("invalid-area")]
    [Arguments("occupied-space")]
    [Arguments("outside-range")]
    [Arguments("nonfinite")]
    [Arguments("object-relative")]
    public Task VehiclePlacement_InvalidPositionPreservesVehicleScrollAndPreparedCosts(string reason) =>
        AssertVehiclePlacementRejected(reason);

    private async Task AssertVehiclePlacementRejected(string reason)
    {
        var (world, currentVehicle, replacementItem) = PrepareVehiclePlacementBoundary();
        var removalSaves = 0;
        world.SlaveManager.SaveForRemoval = _ => { removalSaves++; return false; };
        var placementChecks = 0;
        SlavePlacementRequest? checkedPlacement = null;
        world.SlaveManager.PlacementGeometry = (_, _, request) =>
        {
            placementChecks++;
            checkedPlacement = request;
            return reason == "occupied-space"
                ? ErrorMessageType.SlaveSpawnShipNeedMoreSpace : ErrorMessageType.SlaveSpawnErrorInvalidArea;
        };
        var beforeDetails = new PacketStream();
        replacementItem.WriteDetails(beforeDetails);
        var beforeLocation = replacementItem.SummonLocation;
        var parentSkill = NewSkill();
        var childSkill = new Skill(new SkillTemplate { Id = 15802 });
        var commits = 0;
        parentSkill.CommitLaborBatch = (_, _) => { commits++; return true; };
        var published = false;
        SkillCastTarget target = reason switch
        {
            "missing" => null,
            "unit" => new SkillCastUnitTarget(_owner.ObjId),
            "outside-range" => new SkillCastPositionTarget { PosX = 181, PosY = 100, PosZ = 10 },
            "nonfinite" => new SkillCastPositionTarget { PosX = float.NaN, PosY = 100, PosZ = 10 },
            "object-relative" => new SkillCastPositionTarget
            { PosX = 110, PosY = 100, PosZ = 10, ObjId1 = currentVehicle.ObjId },
            _ => ValidVehicleTarget()
        };

        var accepted = SkillLaborBatch.Run(_owner, parentSkill, true, () =>
        {
            SkillLaborBatch.Current.Consume(_owner.Inventory.Bag, _material.TemplateId, 1, _material);
            SkillLaborBatch.Current.Inventory.TryChangeMoney(_owner, -25);
            SkillLaborBatch.Current.AfterCommit(() => published = true);
            new SpawnSlave().Execute(_owner, new SkillItem { ItemId = replacementItem.Id }, _owner, target,
                null, childSkill, null, DateTime.UtcNow, 0, 0, 0, 0);
        });

        var afterDetails = new PacketStream();
        replacementItem.WriteDetails(afterDetails);
        await Assert.That(accepted).IsFalse();
        await Assert.That(childSkill.Cancelled && parentSkill.Cancelled).IsTrue();
        await Assert.That(placementChecks).IsEqualTo(reason is "invalid-area" or "occupied-space" ? 1 : 0);
        if (checkedPlacement.HasValue)
        {
            await Assert.That(checkedPlacement.Value.Position).IsEqualTo(new Vector3(110, 100, 10));
            await Assert.That(checkedPlacement.Value.Yaw).IsEqualTo(0.75f);
        }
        await Assert.That(removalSaves).IsEqualTo(0);
        await Assert.That(commits).IsEqualTo(0);
        await Assert.That(published).IsFalse();
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Inventory.GetItemById(replacementItem.Id)).IsSameReferenceAs(replacementItem);
        await Assert.That(replacementItem.Count).IsEqualTo(1);
        await Assert.That(afterDetails.GetBytes().SequenceEqual(beforeDetails.GetBytes())).IsTrue();
        await Assert.That(replacementItem.SummonLocation).IsEqualTo(beforeLocation);
        await Assert.That(replacementItem.IsDirty).IsFalse();
        await Assert.That(world.GetAllSlaves().Single()).IsSameReferenceAs(currentVehicle);
        await Assert.That(currentVehicle.AttachmentsRetired).IsFalse();
        await Assert.That(currentVehicle.Despawn).IsEqualTo(DateTime.MinValue);
        await Assert.That(_owner.Packets.Count).IsEqualTo(0);
    }

    private static SkillCastPositionTarget ValidVehicleTarget() => new()
    { PosX = 110, PosY = 100, PosZ = 10, PosRot = 0.75f };

    private (WorldInstance World, Slave CurrentVehicle, SummonSlave ReplacementItem) PrepareVehiclePlacementBoundary()
    {
        var worldManager = new WorldManager(null, null, null, null, null);
        SetInstance(worldManager);
        SetInstance(new ZoneManager(null, null));
        var data = new SlaveGameData();
        SetField(data, "_slaveTemplates", new Dictionary<uint, SlaveTemplate> { [1] = new() { Id = 1 } });
        SetInstance(data);
        var world = new WorldInstance(new WorldTemplate
        { CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 0)
        { Regions = new Region[16, 16] };
        world.Regions[0, 0] = new Region(world, 0, 0, 0);
        world.SlaveManager = new SlaveManager(world);
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [0] = world });
        _owner.ParentWorld = world;
        _owner.Region = world.Regions[0, 0];
        _owner.Transform.Local.SetPosition(100, 100, 10);
        var replacementItem = new SummonSlave
        {
            Id = 2, TemplateId = 300, Template = new SummonSlaveTemplate { Id = 300, SlaveId = 1 },
            Count = 1, OwnerId = _owner.Id, SlotType = SlotType.Inventory, Slot = 1,
            _holdingContainer = _owner.Inventory.Bag, SummonLocation = new Vector3(12, 13, 14)
        };
        var details = new PacketStream();
        details.Write((byte)2).WriteBc(77).Write((byte)0).Write(0L)
            .Write(Enumerable.Range(1, 16).Select(value => (byte)value).ToArray());
        replacementItem.ReadDetails(new PacketStream(details.GetBytes()));
        replacementItem.IsDirty = false;
        _items.Add(replacementItem.Id, replacementItem);
        _owner.Inventory.Bag.Items.Add(replacementItem);
        _owner.Inventory.Bag.UpdateFreeSlotCount();
        var currentVehicle = new Slave
        {
            Id = 77, ObjId = 20, TlId = 30, Summoner = _owner, SummoningItem = replacementItem,
            Hp = 100, ParentWorld = world, Template = data.GetSlaveTemplate(1), IsVisible = true
        };
        currentVehicle.Transform.Local.SetPosition(110, 100, 10);
        world.AddObject(currentVehicle);
        world.Regions[0, 0].AddObject(currentVehicle);
        return (world, currentVehicle, replacementItem);
    }
}
