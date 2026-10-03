using System.Reflection;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PetRepair_LoadedMateMetadataCorrectsTheStoredQuoteBeforeTreatment(bool bank)
    {
        using var graph = new SendGraph();
        var (mate, item) = PreparePersistentMate(graph);
        PrepareStablemaster(graph, mate, item, bank);
        item.DetailLevel = 0;
        item.DetailMateExp = 0;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        Assert.Equal((byte)0, Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id)).DetailLevel);

        var loaded = new CharacterMates(graph.Sender);
        using (var connection = MySQL.CreateConnection())
            loaded.Load(connection);
        loaded.SynchronizeSummonItemDetails();

        Assert.Equal((byte)40, item.DetailLevel);
        Assert.Equal(100, item.DetailMateExp);
        Assert.True(item.DetailInjured);
        Assert.True(item.IsDirty);
        Assert.Equal(4859, Character.GetPetRepairCost(item.DetailLevel));
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var stored = Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
        Assert.Equal((byte)40, stored.DetailLevel);
        Assert.Equal(100, stored.DetailMateExp);
        Assert.True(stored.DetailInjured);
        Assert.Equal(1, ReloadPersistentMate(graph.Sender, item.Id).Hp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PetRepair_UnsummonedBankPetKeepsTreatmentAfterTwoReloads(int savedHp)
    {
        using var graph = new SendGraph();
        var (mate, item) = PreparePersistentMate(graph);
        var npc = PrepareStablemaster(graph, mate, item, true);
        var world = graph.Sender.ParentWorld;
        world.MateManager = new MateManager(world);
        mate.DbInfo.Hp = savedHp;
        mate.DbInfo.Mp = 45;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var cost = Character.GetPetRepairCost(item.DetailLevel);

        Assert.True(graph.Sender.RepairPets(npc, () => graph.Save.TryCommitEconomy([graph.Sender])));

        for (var reload = 0; reload < 2; reload++)
        {
            var loaded = Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
            Assert.False(loaded.DetailInjured);
            Assert.Equal(SlotType.Bank, loaded.SlotType);
            Assert.True(ReloadPersistentMate(graph.Sender, item.Id).Hp > 0);
            Assert.Equal(10000L - cost, Scalar($"SELECT money FROM characters WHERE id={graph.Sender.Id}"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PetRepair_CheckpointKeepsPaymentAndTreatmentTogetherAfterReload(bool bank)
    {
        using var graph = new SendGraph();
        var (mate, item) = PreparePersistentMate(graph);
        var npc = PrepareStablemaster(graph, mate, item, bank);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var cost = Character.GetPetRepairCost(item.DetailLevel);

        Assert.True(graph.Sender.RepairPets(npc, () => graph.Save.TryCommitEconomy([graph.Sender])));

        Assert.Equal(10000L - cost, graph.Sender.Money);
        Assert.Equal(graph.Sender.Money, Scalar($"SELECT money FROM characters WHERE id={graph.Sender.Id}"));
        Assert.False(item.DetailInjured);
        Assert.False(mate.IsInjured);
        Assert.False(mate.IsDowned);
        Assert.False(Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id)).DetailInjured);
        Assert.Equal(mate.Hp, ReloadPersistentMate(graph.Sender, item.Id).Hp);
        Assert.Equal(mate.Mp, ReloadPersistentMate(graph.Sender, item.Id).Mp);

        // A duplicate request cannot debit the same treatment again.
        graph.Sender.RepairPets(npc, () => graph.Save.TryCommitEconomy([graph.Sender]));
        Assert.Equal(10000L - cost, graph.Sender.Money);
        Assert.Equal(graph.Sender.Money, Scalar($"SELECT money FROM characters WHERE id={graph.Sender.Id}"));
    }

    [Theory]
    [InlineData("items")]
    [InlineData("mates")]
    [InlineData("characters")]
    public void PetRepair_SqlFailureRestoresPaymentAndInjuryBeforeOneSuccessfulRetry(string table)
    {
        using var graph = new SendGraph();
        var (mate, item) = PreparePersistentMate(graph);
        var npc = PrepareStablemaster(graph, mate, item, false);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var cost = Character.GetPetRepairCost(item.DetailLevel);
        var trigger = $"pet_repair_fail_{graph.Sender.Id}";
        var id = table switch
        {
            "items" => item.Id,
            "mates" => mate.Id,
            _ => graph.Sender.Id
        };
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW BEGIN IF NEW.id={id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected pet repair failure'; END IF; END");
        try
        {
            Assert.False(graph.Sender.RepairPets(npc, () => graph.Save.TryCommitEconomy([graph.Sender])));
            Assert.Equal(10000L, graph.Sender.Money);
            Assert.Equal(10000L, Scalar($"SELECT money FROM characters WHERE id={graph.Sender.Id}"));
            Assert.True(item.DetailInjured);
            Assert.True(mate.IsInjured);
            Assert.True(mate.IsDowned);
            Assert.Equal(1, mate.Hp);
            Assert.Equal(45, mate.Mp);
            Assert.Equal(1, ReloadPersistentMate(graph.Sender, item.Id).Hp);
            Assert.True(Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id)).DetailInjured);
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }

        Assert.True(graph.Sender.RepairPets(npc, () => graph.Save.TryCommitEconomy([graph.Sender])));
        Assert.Equal(10000L - cost, Scalar($"SELECT money FROM characters WHERE id={graph.Sender.Id}"));
        Assert.False(Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id)).DetailInjured);
        Assert.Equal(mate.Hp, ReloadPersistentMate(graph.Sender, item.Id).Hp);
    }

    private static Npc PrepareStablemaster(SendGraph graph, Mate mate, SummonMate item, bool bank)
    {
        var owner = graph.Sender;
        owner.Hp = 100;
        var world = owner.ParentWorld;
        var npc = new Npc { ObjId = owner.Id + 72, Template = new NpcTemplate { Stabler = true } };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(npc, world);
        world.SetNpc(npc.ObjId, npc);
        owner.CurrentInteractionObject = npc;
        if (bank)
        {
            owner.Inventory.Bag.Items.Remove(item);
            owner.Inventory.Bag.UpdateFreeSlotCount();
            item.SlotType = SlotType.Bank;
            item._holdingContainer = owner.Inventory.Warehouse;
            owner.Inventory.Warehouse.Items.Add(item);
            owner.Inventory.Warehouse.UpdateFreeSlotCount();
        }
        item.DetailInjured = true;
        item.IsDirty = true;
        mate.RestoreInjuryState(item, 1, downed: true);
        return npc;
    }
}
