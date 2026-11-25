using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    public sealed partial class VehicleCargoDropPersistenceTests
    {
        [Fact]
        public void PaddleboatScroll_FreshAndSavedHullUseTheirAuthoredAttachmentPoints()
        {
            using var fixture = new ShipyardGraph();
            PrepareVehicleSummons(fixture);
            var owner = fixture.Graph.Sender;
            var world = fixture.World;
            var physics = new PhysicsManager { SimulationWorld = world };
            typeof(WorldInstance).GetProperty(nameof(WorldInstance.Physics))!.SetValue(world, physics);
            physics.Initialize();

            ConfigurePaddleboat(fixture);
            var scrollTemplate = new SummonSlaveTemplate { Id = 26411, SlaveId = 102, MaxCount = 1 };
            var scroll = new SummonSlave(owner.Id + 42UL, scrollTemplate, 1)
                { OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = 0, _holdingContainer = owner.Inventory.Bag };

            try
            {
                using var pose = new Transform(null);
                pose.Local.SetPosition(100, 100, 40, 0, 0, 0.6f);
                var first = world.SlaveManager.Create(owner, null, 102, scroll, positionOverride: pose);
                CheckPaddleboatFixedParts(first);
                Assert.DoesNotContain(first.AttachedDoodads, part => part.AttachPoint == AttachPointKind.HealPoint0);
                first.Hp = (int)(first.MaxHp * 0.6);
                world.SlaveManager.UpdateSlaveRepairPoints(first);
                CheckVehiclePoint(first, AttachPointKind.HealPoint0, 6107, new(0, -1.8169743f, 1.8278f));
                Assert.True(first.Save());
                var savedId = first.Id;
                Assert.True(savedId > 0);

                pose.Local.SetPosition(150, 160, 40, 0, 0, -0.8f);
                var restored = world.SlaveManager.Create(owner, null, 102, scroll, positionOverride: pose);
                Assert.Equal(savedId, restored.Id);
                Assert.NotEqual(first.ObjId, restored.ObjId);
                Assert.Equal(first.Hp, restored.Hp);
                CheckPaddleboatFixedParts(restored);
                CheckVehiclePoint(restored, AttachPointKind.HealPoint0, 6107, new(0, -1.8169743f, 1.8278f));
            }
            finally
            {
                physics.Dispose();
            }
        }

        private static void PrepareVehicleSummons(ShipyardGraph fixture)
        {
            fixture.EnableSalvage();
            ConfigureCharacterList(fixture);
            var world = fixture.World;
            Array.Clear(world.Template.ZoneKeyByRegions);
            for (var x = 0; x < world.Regions.GetLength(0); x++)
            {
                for (var y = 0; y < world.Regions.GetLength(1); y++)
                    world.Regions[x, y] = new Region(world, x, y, 0);
            }
            world.SlaveManager = new SlaveManager(world);
            var formulas = HousingField<Dictionary<FormulaOwnerType, Dictionary<UnitFormulaKind, UnitFormula>>>(FormulaManager.Instance, "_unitFormulas");
            formulas[FormulaOwnerType.Slave] = formulas[FormulaOwnerType.Character];
            SetField(FormulaManager.Instance, "_unitVariables", new Dictionary<uint, Dictionary<UnitFormulaVariableType, Dictionary<uint, UnitFormulaVariable>>>());
            fixture.EnableVehicleIds();
        }

        private static void ConfigurePaddleboat(ShipyardGraph fixture)
        {
            var slaveData = new SlaveGameData();
            fixture.Replace(slaveData);
            var boat = new SlaveTemplate
            {
                Id = 102, Name = "Winged Love Paddleboat", ModelId = 1287, SlaveKind = SlaveKind.Boat,
                Hp50DoodadCount = 1, Level = 50, Mountable = true
            };
            boat.DoodadBindings.Add(new() { OwnerType = "Slave", OwnerId = 102, DoodadId = 6004, AttachPointId = AttachPointKind.Passenger0, Scale = 1 });
            boat.DoodadBindings.Add(new() { OwnerType = "Slave", OwnerId = 102, DoodadId = 6005, AttachPointId = AttachPointKind.Helms, Scale = 1 });
            boat.HealingPointDoodads.Add(new() { OwnerType = "Slave", OwnerId = 102, DoodadId = 6107, AttachPointId = AttachPointKind.HealPoint0, Scale = 1 });
            HousingField<Dictionary<uint, SlaveTemplate>>(slaveData, "_slaveTemplates")[102] = boat;
            slaveData.LoadSlaveAttachmentPointLocations();
            Assert.Equal(new Vector3(0, -2.20495f, 1.73913f), slaveData.GetAttachPointsForSlave(128)[AttachPointKind.Helms].AsPositionVector());
            var templates = HousingField<Dictionary<uint, DoodadTemplate>>(DoodadManager.Instance, "_templates");
            foreach (var id in new uint[] { 6004, 6005, 6107 })
                templates[id] = new DoodadTemplate { Id = id, FuncGroups = [new DoodadFuncGroups { Id = id, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }] };
            var models = new ModelManager();
            SetField(models, "_modelTypes", new Dictionary<uint, ModelType> { [1287] = new() { SubType = "ShipModel", SubId = 17 } });
            SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>
                { ["ShipModel"] = new() { [17] = new ShipModelV1 { Mass = 800, MassBoxSizeX = 1, MassBoxSizeY = 3, MassBoxSizeZ = 0.5f } } });
            fixture.Replace(models);
        }

        private static void CheckPaddleboatFixedParts(Slave boat)
        {
            CheckVehiclePoint(boat, AttachPointKind.Passenger0, 6004, new(-0.47789f, 0.36952f, 0.93644f));
            CheckVehiclePoint(boat, AttachPointKind.Helms, 6005, new(0.39534f, -0.12314f, 1.41566f));
            Assert.True(SlaveManager.TryGetSeatPosition(boat, AttachPointKind.Driver, null, out var driver));
            Assert.Equal(boat.Transform.TransformPoint(new(0.47789f, 0, 1.41567f)), driver);
        }

        private static void CheckVehiclePoint(Slave vehicle, AttachPointKind kind, uint doodadId, Vector3 position)
        {
            var part = Assert.Single(vehicle.AttachedDoodads, part => part.AttachPoint == kind);
            Assert.Equal(doodadId, part.TemplateId);
            Assert.Same(vehicle.Transform, part.Transform.Parent);
            Assert.Equal(position, part.Transform.Local.Position);
            Assert.Equal(vehicle.Transform.TransformPoint(position), part.Transform.World.Position);
        }
    }
}
