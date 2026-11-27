using System.Numerics;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.Game.Models.StaticValues;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    public sealed partial class VehicleCargoDropPersistenceTests
    {
        [Fact]
        public void ReturnFromCharacterList_WithLoadedShip_CollectsOriginalPackAndRecallsOriginalHull()
        {
            using var fixture = new ShipyardGraph();
            fixture.EnableSalvage();
            ConfigureCargoBindings();
            ConfigureCharacterList(fixture);
            var world = fixture.World;
            var graph = fixture.Graph;
            var original = graph.Sender;
            original.Slots = Enumerable.Range(0, Character.MaxActionSlots).Select(_ => new ActionSlot()).ToArray();
            original.Hp = 90;
            original.Level = 1;
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({original.AccountId},100)");
            var pack = CreateCargoItem(graph);
            var template = new SummonSlaveTemplate { Id = 23398, SlaveId = 75, MaxCount = 1 };
            var scroll = new SummonSlave(original.Id + 42UL, template, 1)
            {
                SlotType = SlotType.Inventory, Slot = 0, SlaveDbId = original.Id + 60,
                OwnerId = original.Id, SummonLocation = new Vector3(100, 120, 0),
                _holdingContainer = original.Inventory.Bag
            };
            HousingField<Dictionary<uint, ItemTemplate>>(graph.Items, "_templates")[template.Id] = template;
            original.Inventory.Bag.Items.Add(scroll);
            original.Inventory.Bag.UpdateFreeSlotCount();
            Assert.True(graph.Items.AddItem(scroll));
            Assert.True(graph.Save.TryCommitEconomy([original]));

            world.SlaveManager = new SlaveManager(world);
            typeof(WorldInstance).GetProperty(nameof(WorldInstance.Physics))!.SetValue(world, new PhysicsManager { SimulationWorld = world });
            for (var x = 0; x < world.Regions.GetLength(0); x++)
            for (var y = 0; y < world.Regions.GetLength(1); y++)
                world.Regions[x, y] = new Region(world, x, y, 0);
            Array.Clear(world.Template.ZoneKeyByRegions);
            var shore = world.Regions[0, 0];
            var hull = new Slave
            {
                Id = scroll.SlaveDbId, ObjId = fixture.ObjectIds.Object.GetNextId(), TlId = 1, ParentWorld = world,
                Template = new SlaveTemplate { Id = 75, PortalTime = 3 }, TemplateId = 75, Hp = 100,
                Name = "Retained Schooner", Summoner = original, OwnerType = BaseUnitType.Character,
                OwnerId = original.Id, OwnerObjId = original.ObjId, SummoningItem = scroll, IsVisible = true, Region = shore
            };
            world.AddObject(hull);
            shore.AddObject(hull);
            var child = new Slave
            {
                Id = hull.Id + 1, ObjId = fixture.ObjectIds.Object.GetNextId(), ParentObj = hull,
                Summoner = original, ParentWorld = world, Name = "Attached cannon", Hp = 100,
                OwnerId = hull.Id, OwnerObjId = hull.ObjId, OwnerType = BaseUnitType.Slave,
                TemplateId = 10, Template = new SlaveTemplate { Id = 10 }, AttachPointId = 9
            };
            child.Transform.Parent = hull.Transform;
            hull.AttachedSlaves.Add(child);
            world.AddObject(child);
            Assert.True(hull.Save());
            var rack = CreateCargoSlot(world, hull, pack, fixture.ObjectIds.Object);
            rack.ParentObjId = hull.ObjId;
            var session = new GameConnection(null);
            Assert.True(session.TryAuthenticate(original.AccountId));
            Assert.True(session.TrySelectCharacter(original));
            original.Connection = session;
            session.Characters.Add(original.Id, original);
            world.SlaveManager.RemoveAndDespawnAllActiveOwnedSlaves(original);
            Assert.Same(hull, world.SlaveManager.GetActiveSlaveByOwnerObjId(original.ObjId));
            Assert.Null(hull.CancelTokenSource);
            session.ReturnToLobby(LeaveWorldTargetType.CharacterSelect);
            session.LoadAccount();
            var selected = session.Characters[original.Id];
            Assert.NotSame(original, selected);
            Assert.Equal(original.AccountId, selected.AccountId);
            Assert.Equal(100, selected.LaborPower);
            Assert.Same(scroll, selected.Inventory.GetItemById(scroll.Id));
            Assert.Null(original.Inventory.GetItemById(scroll.Id));
            Assert.True(CharacterManager.Instance.TrySelectCharacter(session, selected));
            selected.Connection = session;
            selected.ObjId = original.ObjId;
            selected.ParentWorld = world;
            selected.Region = shore;
            Assert.True(WorldManager.Instance.TryAddCharacter(selected));
            world.SlaveManager.ResumeSelectedOwnersVehicle(selected);
            Assert.Same(scroll, hull.SummoningItem);

            // The filled stock rack uses skill 15309, permission 5 and phase 8265.
            var data = DoodadManager.Instance;
            var action = Assert.Single(HousingField<Dictionary<uint, List<DoodadFunc>>>(data, "_funcsByGroups")[8265]);
            action.SkillId = 15309;
            action.PermId = 5;
            HousingField<Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>>(data, "_funcTemplates")
                [nameof(DoodadFuncRecoverItem)][393] = new DoodadFuncRecoverItem { Id = 393 };
            new Use().Execute(selected, null, rack, null, 15309, rack.ObjId);
            Assert.Same(pack, selected.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack));
            Assert.Equal(0UL, rack.ItemId);
            Assert.Equal(8264u, rack.FuncGroupId);
            Assert.Same(selected, hull.Summoner);
            Assert.Same(selected, child.Summoner);
            world.SlaveManager.RemoveAndDespawnAllActiveOwnedSlaves(original);
            Assert.False(world.SlaveManager.Delete(original, hull.ObjId));
            Assert.Same(hull, world.SlaveManager.GetSlaveByObjId(hull.ObjId));

            new CSDespawnSlavePacket { Connection = session }.Read(new PacketStream().WriteBc(hull.ObjId));
            Assert.Null(world.SlaveManager.GetSlaveByObjId(hull.ObjId));
            Assert.Null(world.SlaveManager.GetSlaveByObjId(child.ObjId));
            Assert.Equal(Vector3.Zero, scroll.SummonLocation);
            Assert.True(graph.Save.TryCommitEconomy([selected]));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM slaves WHERE id={hull.Id} AND owner_id={selected.Id} AND summoner={selected.Id} AND item_id={scroll.Id}"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM slaves WHERE id={child.Id} AND owner_id={hull.Id} AND summoner={selected.Id}"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={rack.DbId} AND item_id=0 AND current_phase_id=8264"));
            var items = graph.ReloadLifecycle().Items;
            Assert.Equal(Vector3.Zero, Assert.IsType<SummonSlave>(items.GetItemByItemId(scroll.Id)).SummonLocation);
            var restoredPack = Assert.IsType<Backpack>(items.GetItemByItemId(pack.Id));
            Assert.Equal(pack.UccId, restoredPack.UccId);
            Assert.Equal(SlotType.Equipment, restoredPack.SlotType);
            Assert.Equal((int)EquipmentItemSlot.Backpack, restoredPack.Slot);
        }
    }
}
