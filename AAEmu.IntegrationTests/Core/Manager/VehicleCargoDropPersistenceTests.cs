using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.IntegrationTests.Fixtures;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

// The startup loader reads every System doodad. Give these tests their own database.
[CollectionDefinition(nameof(CargoWreckMySql), DisableParallelization = true)]
public sealed class CargoWreckMySql : ICollectionFixture<GameMySqlFixture>;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Collection(nameof(CargoWreckMySql))]
    [Trait("Category", "GameMySql")]
    public sealed class VehicleCargoDropPersistenceTests
    {
        [Theory]
        [InlineData(AttachPointKind.Box1, 1205, true, true)]
        [InlineData(AttachPointKind.Box13, 1205, true, false)]
        [InlineData(AttachPointKind.Box1, 1385, true, false)]
        [InlineData(AttachPointKind.Box1, 1205, false, false)]
        public void SavedSchoonerRack_RestoresOnlyTheMisplacedBox1(AttachPointKind socket, uint model,
            bool atOldPosition, bool needsCorrection)
        {
            using var fixture = new ShipyardGraph();
            fixture.EnableSalvage();
            ConfigureCargoBindings();
            var data = new SlaveGameData();
            data.LoadSlaveAttachmentPointLocations();
            var oldData = SwapSingleton(data);
            var oldHousing = SwapSingleton(new HousingManager(null, null, null, null, null, null, null,
                null, null, null, null, null, null, null));
            try
            {
                var owner = fixture.Graph.Sender;
                var ship = new Slave { Id = owner.Id + 87, ParentWorld = fixture.World, ModelId = model };
                ship.Transform.Local.SetPosition(100, 120, 40);
                ship.Transform.Local.SetRotation(0, 0, 1.2f);
                var item = CreateCargoItem(fixture.Graph);
                var rack = CreateCargoSlot(fixture.World, ship, item, fixture.ObjectIds.Object);
                // The stock hull has different helpers for Box1 and Box13.
                var box1 = new Vector3(0.73276245f, 11.22975f, 1.88176865f);
                var box13 = new Vector3(0.0151721f, 8.16805f, 2.66416f);
                var saved = atOldPosition ? box13 : new Vector3(1, 2, 3);
                rack.AttachPoint = socket;
                rack.Transform.Local.SetPosition(saved);
                rack.Save();

                var reloaded = new Slave { Id = ship.Id, ParentWorld = fixture.World, ModelId = model };
                reloaded.Transform.Local.SetPosition(100, 120, 40);
                reloaded.Transform.Local.SetRotation(0, 0, 1.2f);
                Assert.Equal(1, fixture.World.SpawnManager.SpawnPersistentDoodads(DoodadOwnerType.Slave, (int)ship.Id, reloaded));
                var loaded = Assert.Single(reloaded.AttachedDoodads);
                Assert.Same(reloaded.Transform, loaded.Transform.Parent);
                var expected = needsCorrection ? box1 : saved;
                Assert.InRange(Vector3.Distance(expected, loaded.Transform.Local.Position), 0f, 0.0001f);
                Assert.Equal(rack.DbId, loaded.DbId);
                Assert.Equal(socket, loaded.AttachPoint);
                Assert.Equal(item.Id, loaded.ItemId);
                loaded.Save();
                Assert.Equal(1, Scalar(FormattableString.Invariant($"""
                    SELECT COUNT(*) FROM doodads WHERE item_id={item.Id} AND id={rack.DbId}
                        AND ABS(x-{expected.X})<0.0001 AND ABS(y-{expected.Y})<0.0001 AND ABS(z-{expected.Z})<0.0001
                    """)));
            }
            finally
            {
                SwapSingleton(oldHousing);
                SwapSingleton(oldData);
            }
        }

        [Theory]
        [InlineData(57, 100, 100)]
        [InlineData(71, 100, 71)]
        [InlineData(0, 29, 0)]
        public void CargoWreck_TransfersTheStoredPackToMeasuredHeightAndReloadsTheSameItem(int floor, int water, int expected)
        {
            using var fixture = new ShipyardGraph();
            fixture.EnableSalvage();
            var graph = fixture.Graph;
            var world = fixture.World;
            AddCargoCoast(world, (ushort)floor, water);
            ConfigureCargoBindings();
            var owner = graph.Sender;
            var vehicle = new Slave { Id = owner.Id + 87, ObjId = owner.Id + 86, ParentWorld = world, TemplateId = 75 };
            vehicle.Transform.Local.SetPosition(100, 100, water);
            var objectIds = new ObjectIdManager();
            Assert.True(objectIds.Initialize());
            var idField = typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previousIds = idField.GetValue(null);
            idField.SetValue(null, objectIds);
            try
            {
                var item = CreateCargoItem(graph);
                var slot = CreateCargoSlot(world, vehicle, item, objectIds);
                Assert.Equal((long)item.Id, Scalar($"SELECT item_id FROM doodads WHERE id={slot.DbId}"));

                vehicle.DestroyAttachedItems();

                var drop = Assert.Single(world.GetAllDoodads());
                Assert.True(drop.IsPersistent);
                Assert.Equal(3054u, drop.TemplateId);
                Assert.Equal(item.Id, drop.ItemId);
                Assert.Equal(item.TemplateId, drop.ItemTemplateId);
                Assert.Equal(item.UccId, drop.UccId);
                Assert.Null(drop.Transform.Parent);
                var point = drop.Transform.World.Position;
                Assert.InRange(point.X, 102f, 104f);
                Assert.InRange(point.Y, 104f, 106f);
                Assert.Equal(84f, world.Template.GeoData.GetHeight(point));
                Assert.True(world.TryGetHeight(point.X, point.Y, out var seabed));
                Assert.Equal((float)floor, seabed);
                Assert.Equal(expected, point.Z);
                Assert.Same(owner.Inventory.SystemContainer, item._holdingContainer);
                Assert.Equal(1, item.Count);
                Assert.Equal(0UL, slot.ItemId);
                Assert.Equal(8264u, slot.FuncGroupId);

                Assert.True(graph.Save.DoSave());
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE item_id={item.Id}"));
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE item_id={item.Id} AND id={drop.DbId} AND z={expected}"));
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={slot.DbId} AND item_id=0 AND item_template_id=0 AND current_phase_id=8264"));
                ReloadAndCollect(graph, vehicle, slot, drop, item);
            }
            finally { idField.SetValue(null, previousIds); }
        }

        private static void ConfigureCargoBindings()
        {
            // Stock storage 3446 on Merchant Ship socket 60 accepts the Lumber Pack.
            // Skill 14513 drops doodad 3054. The player collects it with skill 11361.
            var manager = DoodadManager.Instance;
            var templates = HousingField<Dictionary<uint, DoodadTemplate>>(manager, "_templates");
            templates[3446] = new DoodadTemplate { Id = 3446, FuncGroups =
                [new DoodadFuncGroups { Id = 8264, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }] };
            templates[3054] = new DoodadTemplate { Id = 3054, FuncGroups =
                [new DoodadFuncGroups { Id = 6984, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }] };
            var functions = HousingField<Dictionary<uint, List<DoodadFunc>>>(manager, "_funcsByGroups");
            functions[8265] =
                [new DoodadFunc { FuncType = nameof(DoodadFuncRecoverItem), FuncId = 393, NextPhase = 8264 }];
            var collect = new DoodadFuncRecoverItem { Id = 241 };
            HousingField<Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>>(manager, "_funcTemplates")
                [nameof(DoodadFuncRecoverItem)] = new() { [collect.Id] = collect };
            functions[6984] =
                [new DoodadFunc { FuncType = nameof(DoodadFuncRecoverItem), FuncId = collect.Id, SkillId = 11361, NextPhase = -1 }];
            var skill = new SkillTemplate { Id = 14513 };
            skill.Effects.Add(new SkillEffect { Template = new PutDownBackpackEffect { Id = 1, BackpackDoodadId = 3054 } });
            HousingField<Dictionary<uint, SkillTemplate>>(SkillManager.Instance, "_skills")[skill.Id] = skill;
        }

        private static Backpack CreateCargoItem(SendGraph graph)
        {
            var owner = graph.Sender;
            var template = new BackpackTemplate { Id = 17683, MaxCount = 1, UseSkillId = 14513, FixedGrade = -1 };
            var equipmentId = owner.Inventory.Equipment.ContainerId;
            HousingField<Dictionary<ulong, ItemContainer>>(graph.Items, "_allPersistentContainers")[equipmentId] =
                new EquipmentContainer(owner.Id, SlotType.Equipment, false, owner) { ContainerId = equipmentId, Owner = owner };
            owner.Inventory = new Inventory(owner);
            var system = owner.Inventory.SystemContainer;
            var item = new Backpack(owner.Id + 40UL, template, 1)
            {
                OwnerId = owner.Id, Slot = 0, SlotType = SlotType.System, CreateTime = DateTime.UtcNow,
                UccId = 753, _holdingContainer = system
            };
            HousingField<Dictionary<uint, ItemTemplate>>(graph.Items, "_templates")[template.Id] = template;
            Assert.True(graph.Items.AddItem(item));
            system.Items.Add(item);
            system.UpdateFreeSlotCount();
            Assert.True(graph.Save.TryCommitEconomy([owner]));
            return item;
        }

        private static Doodad CreateCargoSlot(WorldInstance world, Slave vehicle, Backpack item, IObjectIdManager objectIds)
        {
            var slot = DoodadManager.Instance.Create(world, objectIds.GetNextId(), 3446, null, true);
            slot.OwnerType = DoodadOwnerType.Slave;
            slot.OwnerDbId = vehicle.Id;
            slot.AttachPoint = (AttachPointKind)60;
            slot.Transform = vehicle.Transform.CloneAttached(slot);
            slot.Transform.Local.SetPosition(3, 5, 2);
            slot.FuncGroupId = 8265;
            slot.GrowthTime = slot.PlantTime;
            slot.ItemId = item.Id;
            slot.ItemTemplateId = item.TemplateId;
            slot.IsPersistent = true;
            slot.Save();
            slot.Spawn();
            vehicle.AttachedDoodads.Add(slot);
            return slot;
        }

        private static void ReloadAndCollect(SendGraph graph, Slave vehicle, Doodad slot, Doodad drop, Backpack item)
        {
            var owner = graph.Sender;
            var items = graph.ReloadLifecycle().Items;
            var restored = Assert.IsType<Backpack>(items.GetItemByItemId(item.Id));
            Assert.Equal(item.Count, restored.Count);
            Assert.Equal(item.OwnerId, restored.OwnerId);
            Assert.Equal(item.UccId, restored.UccId);
            Assert.NotSame(item, restored);
            Assert.Equal(owner.Inventory.SystemContainer.ContainerId, restored._holdingContainer.ContainerId);
            Assert.Equal(SlotType.System, restored.SlotType);

            var itemData = new ItemGameData();
            SetField(itemData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
            var oldItemData = SwapSingleton(itemData);
            var oldItems = SwapSingleton(items);
            var oldHousing = SwapSingleton(new HousingManager(null, null, null, null, null, null, null,
                null, null, null, null, null, null, null));
            var fresh = HousingPlacementWorld(vehicle.ParentWorld.Id + 1);
            try
            {
                fresh.Regions = new Region[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL];
                var spawns = fresh.SpawnManager = new SpawnManager(fresh);
                var newVehicle = new Slave { Id = vehicle.Id, ParentWorld = fresh };
                Assert.Equal(1, spawns.SpawnPersistentDoodads(DoodadOwnerType.Slave, (int)vehicle.Id, newVehicle));
                var emptySlot = Assert.Single(newVehicle.AttachedDoodads);
                Assert.Equal(slot.DbId, emptySlot.DbId);
                Assert.Equal(0UL, emptySlot.ItemId);
                Assert.Equal(slot.AttachPoint, emptySlot.AttachPoint);
                Assert.Equal(1, spawns.SpawnPersistentDoodads(DoodadOwnerType.System, doSpawn: true));
                var reloadedDrop = Assert.Single(fresh.GetAllDoodads());
                Assert.Equal(drop.DbId, reloadedDrop.DbId);
                Assert.Equal(drop.Transform.World.Position, reloadedDrop.Transform.World.Position);
                Assert.Equal(restored.Id, reloadedDrop.ItemId);
                Assert.Equal(restored.UccId, reloadedDrop.UccId);

                owner.Inventory = new Inventory(owner);
                new RecoverItem().Execute(owner, null, reloadedDrop, null, 11361, reloadedDrop.ObjId);
                Assert.Same(restored, owner.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack));
                Assert.Equal(1, restored.Count);
                Assert.Equal(0UL, reloadedDrop.ItemId);
                Assert.Equal(0U, reloadedDrop.ItemTemplateId);
                Assert.Empty(fresh.GetAllDoodads());
                using var connection = MySQL.CreateConnection();
                using var transaction = connection.BeginTransaction();
                items.Save(connection, transaction);
                transaction.Commit();
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE owner={owner.Id} AND template_id={item.TemplateId}"));
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id} AND count=1 AND slot_type={(int)SlotType.Equipment} AND slot={(int)EquipmentItemSlot.Backpack}"));
                Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE item_id={item.Id} OR id={drop.DbId}"));
            }
            finally
            {
                // This test never starts the instance's spawn threads.
                fresh.SpawnManager = null;
                SwapSingleton(oldHousing);
                SwapSingleton(oldItemData);
                SwapSingleton(oldItems);
            }
        }

        private static void AddCargoCoast(WorldInstance world, ushort seabed, float water)
        {
            world.Water.OceanLevel = water;
            var template = world.Template;
            template.HeightMaxCoefficient = 1;
            var map = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
            for (var x = 48; x <= 54; x++)
            for (var y = 48; y <= 54; y++)
                map[x, y] = seabed;
            var cell = new WorldCell(0, 0, template);
            const BindingFlags properties = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            typeof(WorldCell).GetProperty(nameof(WorldCell.HeightMap), properties)!.SetValue(cell, map);
            typeof(WorldCell).GetProperty(nameof(WorldCell.Loaded), properties)!.SetValue(cell, true);
            template.Cells[0, 0] = cell;
            var bai = new BaseBaiLoader(template);
            var nodes = new NetMissionReader(Stream.Null, 10);
            nodes.NodeDescriptorList.TryAdd(1, new NodeDescriptor(nodes)
                { Id = 1, Pos = new Vector3(103, 105, 84), NavigationType = BaiNavigationType.Triangular });
            bai.NetMissionReaders.Add(nodes);
            template.ZoneBaiLoader.Add(10, bai);
            template.GeoData = new AiGeoDataManager(template);
        }
    }
}
