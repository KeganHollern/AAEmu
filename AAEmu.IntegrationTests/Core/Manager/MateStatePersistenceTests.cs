using System.Reflection;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatePersistence_ActiveStateAndInjuryDetailRoundTripThroughTheRealCheckpoint(bool injured)
    {
        using var graph = new SendGraph();
        var (mate, item) = PreparePersistentMate(graph);
        item.DetailInjured = injured;
        mate.RestoreInjuryState(item, mate.Hp, downed: false);
        mate.Hp = injured ? 1 : 127;
        mate.Mp = 0;
        mate.Name = "Updated mount";
        mate.Level = 42;
        mate.Experience = 123456;
        mate.Mileage = 765;
        item.DetailMateExp = mate.Experience;
        item.DetailLevel = mate.Level;

        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));

        var saved = ReloadPersistentMate(graph.Sender, item.Id);
        Assert.Equal(mate.Id, saved.Id);
        Assert.Equal(graph.Sender.Id, saved.Owner);
        Assert.Equal(mate.Hp, saved.Hp);
        Assert.Equal(0, saved.Mp);
        Assert.Equal("Updated mount", saved.Name);
        Assert.Equal((ushort)42, saved.Level);
        Assert.Equal(123456, saved.Xp);
        Assert.Equal(765, saved.Mileage);
        Assert.True(saved.UpdatedAt > saved.CreatedAt);
        Assert.Same(mate, graph.Sender.ParentWorld.MateManager.GetActiveMateByTlId(graph.Sender.Id, mate.TlId));

        var reloaded = Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id));
        Assert.Equal(injured, reloaded.DetailInjured);
        Assert.Equal(123456, reloaded.DetailMateExp);
        Assert.Equal((byte)42, reloaded.DetailLevel);
        Assert.False(reloaded.IsDirty);
        Assert.Equal(6, Scalar($"SELECT OCTET_LENGTH(details) FROM items WHERE id={item.Id}"));
        Assert.Equal(injured ? 1 : 0, Scalar($"SELECT ORD(SUBSTRING(details,5,1)) FROM items WHERE id={item.Id}"));
    }

    [Fact]
    public void MatePersistence_FailedCheckpointKeepsStoredHealthAndInjuryTogetherAndRetrySavesTheNewState()
    {
        using var graph = new SendGraph();
        var (mate, item) = PreparePersistentMate(graph);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        Assert.Equal(80, ReloadPersistentMate(graph.Sender, item.Id).Hp);

        // Restore the same injury state used at summon. The checkpoint still
        // saves the item detail and the active mate row in one transaction.
        item.DetailInjured = true;
        item.IsDirty = true;
        mate.RestoreInjuryState(item, 0, downed: false);
        mate.Mp = 0;
        Assert.False(graph.Save.TryCommitEconomy([graph.Sender], _ =>
            throw new InvalidOperationException("Forced mate checkpoint failure")));

        Assert.Equal(80, ReloadPersistentMate(graph.Sender, item.Id).Hp);
        Assert.Equal(45, ReloadPersistentMate(graph.Sender, item.Id).Mp);
        Assert.False(Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id)).DetailInjured);
        Assert.True(item.IsDirty);
        Assert.True(mate.IsInjured);
        Assert.Equal(1, mate.Hp);

        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        Assert.Equal(1, ReloadPersistentMate(graph.Sender, item.Id).Hp);
        Assert.Equal(0, ReloadPersistentMate(graph.Sender, item.Id).Mp);
        Assert.True(Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id)).DetailInjured);
        Assert.False(item.IsDirty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void MatePersistence_LegacyNonpositiveHealthBecomesPersistentStandingInjury(int oldHp)
    {
        using var graph = new SendGraph();
        var (mate, item) = PreparePersistentMate(graph);
        mate.Hp = oldHp;
        mate.Mp = 0;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var old = ReloadPersistentMate(graph.Sender, item.Id);
        Assert.Equal(oldHp, old.Hp);
        Assert.False(item.DetailInjured);
        Assert.False(item.IsDirty);

        mate.RestoreInjuryState(item, old.Hp, downed: false);
        Assert.Equal(1, mate.Hp);
        Assert.Equal(0, mate.Mp);
        Assert.True(mate.IsInjured);
        Assert.False(mate.IsDowned);
        Assert.True(item.DetailInjured);
        Assert.True(item.IsDirty);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        Assert.Equal(1, ReloadPersistentMate(graph.Sender, item.Id).Hp);
        Assert.True(Assert.IsType<SummonMate>(graph.ReloadLifecycle().Items.GetItemByItemId(item.Id)).DetailInjured);
    }

    private static (Mate Mate, SummonMate Item) PreparePersistentMate(SendGraph graph)
    {
        var owner = graph.Sender;
        owner.ObjId = owner.Id;
        var world = new WorldInstance(new WorldTemplate { Id = 0, Name = "mate persistence" }, 0, true, 0);
        GC.SuppressFinalize(world);
        world.MateManager = new MateManager(world);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, world);
        owner.Mates = new CharacterMates(owner);
        var item = new SummonMate(owner.Id + 10UL,
            new SummonMateTemplate { Id = owner.Id + 2, MaxCount = 1, FixedGrade = -1 }, 1)
        {
            OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = 0,
            CreateTime = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            _holdingContainer = owner.Inventory.Bag, DetailLevel = 40
        };
        Assert.True(graph.Items.AddItem(item));
        owner.Inventory.Bag.Items.Add(item);
        owner.Inventory.Bag.UpdateFreeSlotCount();
        var saved = new MateDb
        {
            Id = owner.Id + 70, ItemId = item.Id, Owner = owner.Id, Name = "Saved mount", Level = 40,
            Hp = 10, Mp = 10, Xp = 100, Mileage = 200,
            CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
        ((Dictionary<ulong, MateDb>)typeof(CharacterMates)
            .GetField("_mates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner.Mates)!).Add(item.Id, saved);
        var mate = new Mate
        {
            Id = saved.Id, ObjId = owner.Id + 71, TlId = 500,
            OwnerId = owner.Id, OwnerObjId = owner.ObjId, ItemId = item.Id,
            Name = saved.Name, Level = 40, Hp = 80, Mp = 45, Experience = 100, Mileage = 200,
            DbInfo = saved
        };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mate, world);
        world.MateManager.TrackActiveMate(owner.Id, mate);
        return (mate, item);
    }

    private static MateDb ReloadPersistentMate(Character owner, ulong itemId)
    {
        using var connection = MySQL.CreateConnection();
        var loaded = new CharacterMates(owner);
        loaded.Load(connection);
        return Assert.IsType<MateDb>(loaded.GetMateInfo(itemId));
    }
}
