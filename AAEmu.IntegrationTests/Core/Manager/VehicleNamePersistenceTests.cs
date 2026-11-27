using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    public sealed partial class VehicleCargoDropPersistenceTests
    {
        [Fact]
        public void FarmCartOwnerRename_UpdatesBothPlayersAndPersistsForTheOriginalScroll()
        {
            using var fixture = new ShipyardGraph();
            PrepareVehicleSummons(fixture);
            var graph = fixture.Graph;
            var owner = graph.Sender;
            var world = fixture.World;
            typeof(WorldInstance).GetProperty(nameof(WorldInstance.Physics))!
                .SetValue(world, new PhysicsManager { SimulationWorld = world });
            owner.Transform.Local.SetPosition(100, 100, 40);
            owner.Region = world.Regions[0, 0];
            var vehicleData = new SlaveGameData();
            fixture.Replace(vehicleData);
            var cart = new SlaveTemplate
            {
                Id = 555, ModelId = 8000009, Name = "Farm Cart",
                SlaveKind = SlaveKind.Machine, Mountable = true, Level = 50
            };
            HousingField<Dictionary<uint, SlaveTemplate>>(vehicleData, "_slaveTemplates").Add(cart.Id, cart);
            var template = new SummonSlaveTemplate { Id = 36293, SlaveId = cart.Id, MaxCount = 1 };
            HousingField<Dictionary<uint, ItemTemplate>>(graph.Items, "_templates").Add(template.Id, template);
            var firstScroll = AddScroll(1);
            var otherScroll = AddScroll(2);
            Assert.True(graph.Save.TryCommitEconomy([owner]));
            using var pose = new Transform(null);
            pose.Local.SetPosition(105, 100, 40);

            var first = world.SlaveManager.Create(owner, null, cart.Id, firstScroll, positionOverride: pose);
            Assert.Equal(cart.Name, first.Name);
            var ownerUpdates = ListenForNames(owner);
            var neighborUpdates = ListenForNames(graph.Receiver);
            Submit(graph.Receiver, first.TlId, "stranger");
            Submit(owner, first.TlId, "a");
            Assert.Equal(cart.Name, first.Name);
            Assert.Empty(ownerUpdates);
            Assert.Empty(neighborUpdates);

            Submit(owner, first.TlId, "aster");
            Assert.Equal("Aster", first.Name);
            var expected = (first.ObjId, "Aster");
            Assert.Equal(expected, Assert.Single(ownerUpdates));
            Assert.Equal(expected, Assert.Single(neighborUpdates));
            Assert.True(world.SlaveManager.Delete(owner, first.ObjId));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM slaves WHERE id={first.Id} AND name='Aster' " +
                $"AND owner_id={owner.Id} AND summoner={owner.Id} AND item_id={firstScroll.Id}"));

            var other = world.SlaveManager.Create(owner, null, cart.Id, otherScroll, positionOverride: pose);
            Assert.NotEqual(first.Id, other.Id);
            Assert.Equal(cart.Name, other.Name);
            Assert.NotEqual(first.TlId, other.TlId);
            Submit(owner, first.TlId, "stale");
            Assert.Equal(cart.Name, other.Name);
            Assert.True(world.SlaveManager.Delete(owner, other.ObjId));

            Assert.True(graph.Save.TryCommitEconomy([owner]));
            var reloadedItems = graph.ReloadLifecycle().Items;
            var savedScroll = Assert.IsType<SummonSlave>(reloadedItems.GetItemByItemId(firstScroll.Id));
            Assert.Equal(first.Id, savedScroll.SlaveDbId);
            var restored = world.SlaveManager.Create(owner, null, cart.Id, savedScroll, positionOverride: pose);
            Assert.NotEqual(first.ObjId, restored.ObjId);
            Assert.Equal(first.Id, restored.Id);
            Assert.Equal("Aster", restored.Name);

            List<(uint ObjectId, string Name)> ListenForNames(Character player)
            {
                var updates = new List<(uint, string)>();
                var session = new Mock<ISession>();
                session.Setup(s => s.SendPacket(It.IsAny<byte[]>())).Callback<byte[]>(bytes =>
                {
                    if (BitConverter.ToUInt16(bytes, 6) != SCOffsets.SCUnitNameChangedPacket)
                        return;
                    var packet = new PacketStream(bytes[8..]);
                    updates.Add((packet.ReadBc(), packet.ReadString()));
                });
                player.ParentWorld = world;
                player.ObjId = player.Id;
                player.Transform.Local.SetPosition(100, 100, 40);
                player.Region = world.Regions[0, 0];
                player.Region.AddObject(player);
                player.Connection = new GameConnection(session.Object) { ActiveChar = player };
                return updates;
            }

            static void Submit(Character player, ushort tlId, string name)
            {
                var request = new PacketStream().Write(tlId).Write(name);
                new CSChangeSlaveNamePacket { Connection = player.Connection }.Read(request);
            }

            SummonSlave AddScroll(byte slot)
            {
                var scroll = new SummonSlave(owner.Id + 40UL + slot, template, 1)
                {
                    OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = slot, _holdingContainer = owner.Inventory.Bag
                };
                owner.Inventory.Bag.Items.Add(scroll);
                owner.Inventory.Bag.UpdateFreeSlotCount();
                Assert.True(graph.Items.AddItem(scroll));
                return scroll;
            }
        }
    }
}
