using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(123.25f, 456.75f)]
    [InlineData(0f, 456.75f)]
    public void VehiclePlacement_SavedCoordinatesSurviveItemReloadAndExplicitClear(float x, float y)
    {
        using var fixture = new VehicleRepairGraph(4, 1000, 5);
        var graph = fixture.Graph;
        var item = fixture.Vehicle;
        var prefix = VehiclePlacementDetails(item)[..13];
        item.SummonLocation = new Vector3(x, y, 10);
        item.IsDirty = true;

        Assert.True(graph.Save.DoSave());

        var saved = Assert.IsType<SummonSlave>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
        Assert.Equal(new Vector3(x, y, 0), saved.SummonLocation);
        Assert.Equal(x != 0 && y != 0, saved.HasSummonLocation);
        Assert.Equal(item.SlaveDbId, saved.SlaveDbId);
        Assert.Equal(item.OwnerId, saved.OwnerId);
        Assert.Equal(prefix, VehiclePlacementDetails(saved)[..13]);
        Assert.Equal(VehiclePlacementDetails(item), ReadVehiclePlacementDetails(item.Id));

        item.ClearSummonLocation();
        item.IsDirty = true;
        Assert.True(graph.Save.DoSave());

        var cleared = Assert.IsType<SummonSlave>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
        Assert.False(cleared.HasSummonLocation);
        Assert.Equal(Vector3.Zero, cleared.SummonLocation);
        Assert.Equal(prefix, VehiclePlacementDetails(cleared)[..13]);
        Assert.Equal(new byte[16], ReadVehiclePlacementDetails(item.Id)[13..]);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM slaves WHERE id={item.SlaveDbId} AND item_id={item.Id}"));
    }

    [Theory]
    [InlineData("invalid-area")]
    [InlineData("occupied-space")]
    [InlineData("outside-range")]
    public void VehiclePlacement_RejectionPreservesDurableScrollVehicleAndPreparedCosts(string reason)
    {
        using var fixture = new VehicleRepairGraph(4, 1000, 5);
        var graph = fixture.Graph;
        var player = graph.Sender;
        var item = fixture.Vehicle;
        item.IsDestroyed = 0;
        item.SummonLocation = new Vector3(300, 300, 0);
        item.IsDirty = true;
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var beforeDetails = ReadVehiclePlacementDetails(item.Id);
        var beforeMoney = player.Money;
        using var scene = new VehiclePlacementScene(fixture);
        var world = scene.World;
        var active = scene.Active;
        var saves = 0;
        world.SlaveManager.SaveForRemoval = _ => { saves++; return false; };
        var geometryChecks = 0;
        world.SlaveManager.PlacementGeometry = (_, _, _) =>
        {
            geometryChecks++;
            return reason == "occupied-space"
                ? ErrorMessageType.SlaveSpawnShipNeedMoreSpace : ErrorMessageType.SlaveSpawnErrorInvalidArea;
        };
        var parent = new Skill(new SkillTemplate { Id = 901, ConsumeLaborPower = 10 });
        var child = new Skill(new SkillTemplate { Id = 15802 });
        var commits = 0;
        parent.CommitLaborBatch = (_, write) =>
        {
            commits++;
            return graph.Save.TryCommitEconomy([player], write);
        };

        var accepted = SkillLaborBatch.Run(player, parent, true, () =>
        {
            SkillLaborBatch.Current.Consume(player.Inventory.Bag, fixture.Kit.TemplateId, 1, fixture.Kit);
            SkillLaborBatch.Current.Inventory.TryChangeMoney(player, -25);
            new SpawnSlave().Execute(player, new SkillItem { ItemId = item.Id }, player,
                new SkillCastPositionTarget { PosX = reason == "outside-range" ? 181 : 110, PosY = 100, PosZ = 10 },
                null, child, null, DateTime.UtcNow, 0, 0, 0, 0);
        });

        Assert.False(accepted);
        Assert.True(parent.Cancelled && child.Cancelled);
        Assert.Equal(reason == "outside-range" ? 0 : 1, geometryChecks);
        Assert.Equal(0, saves);
        Assert.Equal(0, commits);
        Assert.Same(active, Assert.Single(world.GetAllSlaves()));
        Assert.False(active.AttachmentsRetired);
        Assert.Equal(DateTime.MinValue, active.Despawn);
        Assert.Same(item, player.Inventory.GetItemById(item.Id));
        Assert.Equal(beforeDetails, VehiclePlacementDetails(item));
        Assert.Equal(beforeDetails, ReadVehiclePlacementDetails(item.Id));
        Assert.False(item.IsDirty);
        Assert.Equal(beforeMoney, player.Money);
        Assert.Equal(beforeMoney, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
        Assert.Equal(20, player.LaborPower);
        Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Equal(5, fixture.Kit.Count);
        Assert.Equal(5, Scalar($"SELECT count FROM items WHERE id={fixture.Kit.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM slaves WHERE id={item.SlaveDbId} AND item_id={item.Id} AND hp=0 AND mp=7"));
        var reloaded = Assert.IsType<SummonSlave>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
        Assert.Equal(beforeDetails, VehiclePlacementDetails(reloaded));
    }

    [Theory]
    [InlineData("player", false)]
    [InlineData("visibility-timeout", false)]
    [InlineData("cleanup", false)]
    [InlineData("cleanup", true)]
    public void VehiclePlacement_RemovalClearsOfflineScrollOnPeriodicSaveAndRetriesFailure(string removal, bool failFirstSave)
    {
        using var fixture = new VehicleRepairGraph(4, 1000, 5);
        var graph = fixture.Graph;
        var player = graph.Sender;
        var item = fixture.Vehicle;
        item.IsDestroyed = 0;
        item.SummonLocation = new Vector3(300, 300, 0);
        item.IsDirty = true;
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var beforeDetails = ReadVehiclePlacementDetails(item.Id);
        using var scene = new VehiclePlacementScene(fixture);
        var world = scene.World;
        var active = scene.Active;
        player.IsOnline = false;
        if (removal == "visibility-timeout")
        {
            active.Region.RemoveObject(active);
            active.RecordOwnerVisibility(player, false, DateTime.UtcNow.AddSeconds(-301));
        }

        if (removal == "cleanup")
            world.SlaveManager.RemoveAndDespawnAllActiveOwnedSlaves(player);
        else
            Assert.True(removal == "player"
                ? world.SlaveManager.Delete(player, active.ObjId)
                : world.SlaveManager.RemoveActiveSlave(player, active.TlId));

        Assert.Empty(world.GetAllSlaves());
        Assert.True(active.AttachmentsRetired);
        Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
        Assert.False(item.HasSummonLocation);
        Assert.True(item.IsDirty);
        Assert.Equal(beforeDetails, ReadVehiclePlacementDetails(item.Id));
        Assert.Equal(100, Scalar($"SELECT hp FROM slaves WHERE id={item.SlaveDbId}"));
        if (failFirstSave)
        {
            var trigger = $"vehicle_location_{player.Id}";
            Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON items FOR EACH ROW BEGIN " +
                $"IF NEW.id={item.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Forced vehicle location save failure'; END IF; END");
            try
            {
                Assert.False(graph.Save.DoSave());
                Assert.True(item.IsDirty);
                Assert.Equal(beforeDetails, ReadVehiclePlacementDetails(item.Id));
            }
            finally
            {
                Execute($"DROP TRIGGER {trigger}");
            }
        }

        // SendGraph enumerates no online characters. The ordinary periodic path
        // must still save this globally registered scroll after delayed cleanup.
        Assert.True(graph.Save.DoSave());
        Assert.False(item.IsDirty);
        var reloaded = Assert.IsType<SummonSlave>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
        Assert.False(reloaded.HasSummonLocation);
        Assert.Equal(Vector3.Zero, reloaded.SummonLocation);
        Assert.Equal(beforeDetails[..13], VehiclePlacementDetails(reloaded)[..13]);
        Assert.Equal(new byte[16], ReadVehiclePlacementDetails(item.Id)[13..]);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM slaves WHERE id={item.SlaveDbId} AND item_id={item.Id}"));
    }

    private sealed class VehiclePlacementScene : IDisposable
    {
        private readonly WorldConfig _previousConfig = AppConfiguration.Instance.World;
        public WorldInstance World { get; }
        public Slave Active { get; }

        public VehiclePlacementScene(VehicleRepairGraph fixture)
        {
            AppConfiguration.Instance.World = new WorldConfig();
            var player = fixture.Graph.Sender;
            World = HousingPlacementWorld(0);
            Array.Clear(World.Template.ZoneKeyByRegions);
            World.Regions = new Region[16, 16];
            World.Regions[0, 0] = new Region(World, 0, 0, 0);
            World.SlaveManager = new SlaveManager(World);
            World.SpawnManager = new SpawnManager(World);
            typeof(WorldInstance).GetProperty(nameof(WorldInstance.Physics))!
                .SetValue(World, new PhysicsManager { SimulationWorld = World });
            SetParentWorld(player, World);
            player.Region = World.Regions[0, 0];
            player.Transform.Local.SetPosition(100, 100, 10);
            Active = new Slave
            {
                Id = fixture.Vehicle.SlaveDbId, ObjId = player.ObjId + 1, TlId = 30,
                Summoner = player, SummoningItem = fixture.Vehicle, OwnerId = player.Id,
                Hp = 100, Mp = 7, ParentWorld = World, TemplateId = fixture.VehicleTemplate.Id,
                Template = fixture.VehicleTemplate, IsVisible = true, Region = World.Regions[0, 0]
            };
            Active.Transform.Local.SetPosition(110, 100, 10);
            World.AddObject(Active);
            Active.Region.AddObject(Active);
        }

        public void Dispose()
        {
            World.SpawnManager = null;
            AppConfiguration.Instance.World = _previousConfig;
        }
    }

    private static byte[] VehiclePlacementDetails(SummonSlave item)
    {
        var stream = new PacketStream();
        item.WriteDetails(stream);
        return stream.GetBytes();
    }

    private static byte[] ReadVehiclePlacementDetails(ulong itemId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT details FROM items WHERE id=@id";
        command.Parameters.AddWithValue("@id", itemId);
        return (byte[])command.ExecuteScalar();
    }
}
