using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(true, false, "success")]
    [InlineData(true, false, "failed_commit")]
    [InlineData(true, false, "no_labor")]
    [InlineData(true, false, "reserved")]
    [InlineData(true, false, "bank")]
    [InlineData(false, false, "success")]
    [InlineData(false, false, "failed_commit")]
    [InlineData(false, false, "no_labor")]
    [InlineData(false, true, "success")]
    [InlineData(false, true, "failed_commit")]
    public void PlayerDoodad_PlacementPersistsOnlyWithItsExactItemAndLabor(bool stackable, bool coffer, string outcome)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        using var reservation = new TradeReservation();
        var oldZones = SwapSingleton(new ZoneManager(null, null));
        var oldWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, ExpRate = 1 };
        var containerIdField = typeof(ContainerIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldContainerIds = containerIdField.GetValue(null);
        var containerIds = new ContainerIdManager();
        Assert.True(containerIds.Initialize());
        containerIdField.SetValue(null, containerIds);
        try
        {
            var player = graph.Sender;
            var labor = outcome == "no_labor" ? 0 : 20;
            player.InitializeLaborCache((short)labor, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},{labor}) ON DUPLICATE KEY UPDATE labor={labor}");
            var item = stackable ? graph.AddItem(0) : graph.AddEquipment(0);
            item.Count = stackable ? 2 : 1;
            item.Template.UseSkillId = 50;
            var alternative = stackable ? graph.AddEquipment(1) : graph.AddItem(1);
            var alternativeCount = alternative.Count;
            SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate>
                { [50] = new() { Id = 50, ConsumeLaborPower = 10, ActabilityGroupId = 43 } });
            SetField(graph.Items, "_itemDoodadTemplates", new Dictionary<uint, ItemDoodadTemplate>
                { [1] = new() { DoodadId = 1, ItemIds = [item.TemplateId, alternative.TemplateId] } });
            if (outcome == "bank")
            {
                player.Inventory.Bag.Items.Remove(item);
                item._holdingContainer = player.Inventory.Warehouse;
                item.SlotType = SlotType.Bank;
                player.Inventory.Warehouse.Items.Add(item);
            }
            if (outcome == "reserved")
                Assert.True(reservation.TryReserve(item, item.Count));
            Assert.True(graph.Save.TryCommitEconomy([player]));
            var world = new WorldInstance(new WorldTemplate { Id = 0, Name = "placement" }, 0, true, uint.MaxValue);
            typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, world);
            var objectIds = new Mock<IObjectIdManager>();
            objectIds.Setup(ids => ids.GetNextId()).Returns(256);
            var doodadIds = new Mock<IDoodadIdManager>();
            var dbId = player.Id + 50;
            doodadIds.Setup(ids => ids.GetNextId()).Returns(dbId);
            var manager = new DoodadManager(objectIds.Object, doodadIds.Object, graph.Items,
                new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>()), null);
            DoodadTemplate template = coffer ? new DoodadCofferTemplate { Id = 1, Capacity = 10 } : new DoodadTemplate { Id = 1 };
            SetField(manager, "_templates", new Dictionary<uint, DoodadTemplate> { [1] = template });
            var oldManager = SwapSingleton(manager);
            Doodad published = null;
            var uses = 0;
            player.Events.OnItemUse += (_, _) => uses++;
            ulong preparedContainerId = 0;
            var containers = (Dictionary<ulong, ItemContainer>)typeof(ItemManager)
                .GetField("_allPersistentContainers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
            manager.PublishPlayerDoodadPlacement = doodad =>
            {
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={dbId}"));
                published = doodad;
            };
            manager.CommitPlayerDoodadPlacement = (_, write) => graph.Save.TryCommitEconomy([player], context =>
            {
                Assert.Null(published);
                Assert.Equal(0, uses);
                preparedContainerId = containers.Values.OfType<CofferContainer>().SingleOrDefault()?.ContainerId ?? 0;
                write(context);
                if (outcome == "failed_commit")
                    throw new InvalidOperationException("Injected failure after new doodad and labor writes");
            });
            try
            {
                var result = manager.CreatePlayerDoodad(player, 1, 100, 200, 300, 0.5f, 1, item.Id, laborCost: 10);
                var success = outcome == "success";
                Assert.Equal(success, result != null);
                Assert.Same(result, published);
                Assert.Equal(success ? 10 : labor, player.LaborPower);
                Assert.Equal(success ? 10 : labor, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
                Assert.Equal(success ? 1 : 0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={dbId}"));
                Assert.Equal(success ? 1 : 0, uses);
                Assert.Equal(alternativeCount, alternative.Count);
                Assert.Equal(alternativeCount, Scalar($"SELECT count FROM items WHERE id={alternative.Id}"));
                Assert.Equal(stackable ? success ? 1 : 2 : 1, item.Count);
                var expectedSlot = outcome == "bank" ? SlotType.Bank : success && !stackable ? SlotType.System : SlotType.Inventory;
                Assert.Equal(expectedSlot, item.SlotType);
                var reloaded = graph.ReloadLifecycle().Items.GetItemByItemId(item.Id);
                Assert.NotNull(reloaded);
                Assert.Equal(item.Count, reloaded.Count);
                Assert.Equal(expectedSlot, reloaded.SlotType);
                if (!stackable)
                {
                    Assert.Equal(753U, reloaded.UccId);
                    Assert.Equal(812U, reloaded.ImageItemTemplateId);
                }
                if (success)
                {
                    Assert.Equal(stackable ? 0 : (long)item.Id, Scalar($"SELECT item_id FROM doodads WHERE id={dbId}"));
                    Assert.Equal(item.TemplateId, result.ItemTemplateId);
                    Assert.Equal(100f, result.Transform.Local.Position.X);
                    Assert.Equal(300f, result.Transform.Local.Position.Z);
                }
                if (coffer)
                {
                    Assert.NotEqual(0UL, preparedContainerId);
                    Assert.Equal(success ? 1 : 0, Scalar($"SELECT COUNT(*) FROM item_containers WHERE container_id={preparedContainerId}"));
                    Assert.Equal(success, graph.Items.GetItemContainerByDbId(preparedContainerId) != null);
                }
                var allocated = success || outcome == "failed_commit";
                objectIds.Verify(ids => ids.GetNextId(), allocated ? Times.Once() : Times.Never());
                doodadIds.Verify(ids => ids.GetNextId(), allocated ? Times.Once() : Times.Never());
                objectIds.Verify(ids => ids.ReleaseId(256), outcome == "failed_commit" ? Times.Once() : Times.Never());
                doodadIds.Verify(ids => ids.ReleaseId(dbId), outcome == "failed_commit" ? Times.Once() : Times.Never());
            }
            finally
            {
                SwapSingleton(oldManager);
            }
        }
        finally
        {
            SwapSingleton(oldZones);
            AppConfiguration.Instance.World = oldWorldConfig;
            containerIdField.SetValue(null, oldContainerIds);
        }
    }
}
