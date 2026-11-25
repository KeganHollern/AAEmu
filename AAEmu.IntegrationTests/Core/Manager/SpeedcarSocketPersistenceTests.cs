using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    public sealed partial class VehicleCargoDropPersistenceTests
    {
        [Theory]
        [InlineData(106, 1323, 26874, 6445, false)]
        [InlineData(112, 1371, 27153, 6771, false)]
        [InlineData(116, 1406, 28204, 6772, false)]
        [InlineData(106, 1323, 26874, 6445, true)]
        [InlineData(112, 1371, 27153, 6771, true)]
        [InlineData(116, 1406, 28204, 6772, true)]
        public void SpeedcarScroll_RestoresBothRacksWithTheSameCargo(uint vehicleId, uint modelId,
            uint scrollId, uint hoodId, bool fromOldCatalog)
        {
            using var fixture = new ShipyardGraph();
            PrepareVehicleSummons(fixture);
            ConfigureCargoBindings();
            var cars = new SlaveGameData();
            fixture.Replace(cars);
            cars.LoadSlaveAttachmentPointLocations();
            var car = new SlaveTemplate
            {
                Id = vehicleId, ModelId = modelId, Name = "Speedcar", SlaveKind = SlaveKind.Tank,
                Level = 50, Mountable = true, HasPassengerSeat = true, Hp75DoodadCount = 1
            };
            car.DoodadBindings.Add(new() { OwnerType = "Slave", OwnerId = vehicleId, DoodadId = hoodId, AttachPointId = AttachPointKind.Sail0, Scale = 1 });
            var rackScale = vehicleId == 106 ? 0.42f : 0.4f;
            foreach (var point in new[] { AttachPointKind.Box0, AttachPointKind.Box1 })
                car.DoodadBindings.Add(new() { OwnerType = "Slave", OwnerId = vehicleId, DoodadId = 3446, AttachPointId = point, Scale = rackScale, Persist = true });
            car.HealingPointDoodads.Add(new() { OwnerType = "Slave", OwnerId = vehicleId, DoodadId = 6444, AttachPointId = AttachPointKind.HealPoint0 });
            HousingField<Dictionary<uint, SlaveTemplate>>(cars, "_slaveTemplates")[vehicleId] = car;
            var doodads = HousingField<Dictionary<uint, DoodadTemplate>>(DoodadManager.Instance, "_templates");
            foreach (var id in new[] { hoodId, 6444u })
                doodads[id] = new DoodadTemplate { Id = id, FuncGroups = [new DoodadFuncGroups { Id = id, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }] };

            var owner = fixture.Graph.Sender;
            var item = CreateCargoItem(fixture.Graph);
            var scroll = new SummonSlave(owner.Id + 42UL, new SummonSlaveTemplate { Id = scrollId, SlaveId = vehicleId, MaxCount = 1 }, 1)
                { OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = 1, _holdingContainer = owner.Inventory.Bag };
            if (fromOldCatalog)
            {
                // The previous catalog contained the passenger socket alone. Save the racks through that path.
                var old = cars.GetAttachPointsForSlave(modelId);
                foreach (var socket in old.Keys.Where(p => p != AttachPointKind.Passenger0).ToArray())
                    old.Remove(socket);
            }
            using var pose = new Transform(null);
            pose.Local.SetPosition(100, 100, 40, 0, 0, 0.7f);
            var first = fixture.World.SlaveManager.Create(owner, null, vehicleId, scroll, positionOverride: pose);
            var left = first.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Box0);
            var right = first.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Box1);
            Assert.DoesNotContain(first.AttachedDoodads, d => d.AttachPoint == AttachPointKind.HealPoint0);
            if (fromOldCatalog)
            {
                Assert.Equal(Vector3.Zero, left.Transform.Local.Position);
                Assert.Equal(Vector3.Zero, right.Transform.Local.Rotation);
            }
            else
                CheckSpeedcar(first, hoodId, rackScale);
            left.FuncGroupId = 8265;
            left.ItemId = item.Id;
            left.ItemTemplateId = item.TemplateId;
            left.Save();
            right.Save();
            first.Hp = first.MaxHp * 80 / 100;
            fixture.World.SlaveManager.UpdateSlaveRepairPoints(first);
            Assert.True(first.Save());
            if (fromOldCatalog)
                cars.LoadSlaveAttachmentPointLocations();

            pose.Local.SetPosition(120, 140, 40, 0, 0, -0.8f);
            var restored = fixture.World.SlaveManager.Create(owner, null, vehicleId, scroll, positionOverride: pose);
            Assert.Equal(first.Id, restored.Id);
            Assert.NotEqual(first.ObjId, restored.ObjId);
            Assert.Equal(first.Hp, restored.Hp);
            CheckSpeedcar(restored, hoodId, rackScale);
            CheckVehiclePoint(restored, AttachPointKind.HealPoint0, 6444, new(0.69189f, -0.13275997f, 1.07908f));
            var loaded = restored.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Box0);
            Assert.Equal(left.DbId, loaded.DbId);
            Assert.Equal(item.Id, loaded.ItemId);
            Assert.Equal(item.UccId, loaded.UccId);
            Assert.Equal(8265u, loaded.FuncGroupId);
            var other = restored.AttachedDoodads.Single(d => d.AttachPoint == AttachPointKind.Box1);
            Assert.Equal(right.DbId, other.DbId);
            Assert.Equal(0UL, other.ItemId);
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={item.Id} AND owner={owner.Id} AND count=1"));
            // A normal doodad save writes the corrected position and orientation on the original row.
            loaded.Save();
            Assert.Equal(1, Scalar(FormattableString.Invariant($"""
                SELECT COUNT(*) FROM doodads WHERE item_id={item.Id} AND id={left.DbId} AND current_phase_id=8265
                    AND ABS(x+0.52005464)<0.00001 AND ABS(y+2.167765)<0.00001 AND ABS(z-0.6830052)<0.00001
                    AND ABS(roll+PI()/2)<0.00001 AND ABS(pitch+RADIANS(55))<0.00001 AND ABS(yaw+PI()/2)<0.00001
                """)));

            restored.Hp = restored.MaxHp * 60 / 100;
            fixture.World.SlaveManager.UpdateSlaveRepairPoints(restored);
            Assert.DoesNotContain(restored.AttachedDoodads, d => d.AttachPoint == AttachPointKind.HealPoint0);
            restored.Hp = restored.MaxHp * 20 / 100;
            fixture.World.SlaveManager.UpdateSlaveRepairPoints(restored);
            CheckVehiclePoint(restored, AttachPointKind.HealPoint0, 6444, new(0.69189f, -0.13275997f, 1.07908f));
            CheckSpeedcar(restored, hoodId, rackScale);

            // Neither a saved offset nor a rotation at the origin matches the old unplaced rack.
            loaded.Transform.Local.SetPosition(1, 2, 3, 0, 0, 0);
            other.Transform.Local.SetPosition(0, 0, 0, 0.25f, 0, 0);
            loaded.Save();
            other.Save();
            var third = fixture.World.SlaveManager.Create(owner, null, vehicleId, scroll, positionOverride: pose);
            var occupiedRack = third.AttachedDoodads.Single(d => d.DbId == left.DbId);
            var emptyRack = third.AttachedDoodads.Single(d => d.DbId == right.DbId);
            Assert.Equal(new Vector3(1, 2, 3), occupiedRack.Transform.Local.Position);
            Assert.Equal(item.Id, occupiedRack.ItemId);
            Assert.Equal(Vector3.Zero, emptyRack.Transform.Local.Position);
            Assert.Equal(new Vector3(0.25f, 0, 0), emptyRack.Transform.Local.Rotation);
        }

        private static void CheckSpeedcar(Slave car, uint hoodId, float scale)
        {
            CheckVehiclePoint(car, AttachPointKind.Sail0, hoodId, new(0, 0.87057996f, 1.18104f));
            CheckVehiclePoint(car, AttachPointKind.Box0, 3446, new(-0.52005464f, -2.167765f, 0.6830052f));
            CheckVehiclePoint(car, AttachPointKind.Box1, 3446, new(0.52022153f, -2.167765f, 0.6830052f));
            var points = SlaveGameData.Instance.GetAttachPointsForSlave(car.ModelId);
            foreach (var rack in car.AttachedDoodads.Where(d => d.TemplateId == 3446))
            {
                var socket = points[rack.AttachPoint];
                Assert.Equal(new Vector3(socket.Roll, socket.Pitch, socket.Yaw), rack.Transform.Local.Rotation);
                Assert.Equal(scale, rack.Scale);
            }
            Assert.True(SlaveManager.TryGetSeatPosition(car, AttachPointKind.Driver, null, out var driver));
            Assert.Equal(car.Transform.TransformPoint(new(-0.48502997f, -1.2582501f, 0.75753f)), driver);
            Assert.True(SlaveManager.TryGetSeatPosition(car, AttachPointKind.Passenger0, null, out var passenger));
            Assert.Equal(car.Transform.TransformPoint(new(0.4996243f, -0.6405379f, 0.3236309f)), passenger);
        }
    }
}
