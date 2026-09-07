using System.Reflection;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World.Zones;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class ZoneConflictPersistenceTests
{
    [Fact]
    public void SaveAndRestore_AllPhasesRetainCountsAndUtcDeadlines()
    {
        var expected = Enum.GetValues<ZoneConflictType>().ToDictionary(
            state => (ushort)(65000 + (byte)state),
            state => new ZoneConflictSnapshot(state, state < ZoneConflictType.Conflict ? 37u : 0u,
                state < ZoneConflictType.Conflict ? DateTime.MinValue : new DateTime(2030, 1, 1, 12, 30, 15, 123, DateTimeKind.Utc)));
        var writer = CreateManager(expected);
        using var connection = MySQL.CreateConnection();
        using (var transaction = connection.BeginTransaction())
        {
            Assert.Equal(expected.Count, writer.Save(connection, transaction));
            transaction.Commit();
        }

        var reader = CreateManager(expected.ToDictionary(pair => pair.Key,
            _ => new ZoneConflictSnapshot(ZoneConflictType.Tension, 0, DateTime.MinValue)));
        reader.RestoreStates(connection);
        foreach (var conflict in reader.GetConflicts())
            Assert.Equal(expected[conflict.ZoneGroupId], conflict.GetSnapshot());

        // Reusing the same rows updates both a count and a nullable deadline.
        expected[65000] = new(ZoneConflictType.Danger, 83, DateTime.MinValue);
        expected[65005] = new(ZoneConflictType.Tension, 0, DateTime.MinValue);
        writer = CreateManager(expected);
        using (var transaction = connection.BeginTransaction())
        {
            writer.Save(connection, transaction);
            transaction.Commit();
        }
        reader.RestoreStates(connection);
        foreach (var conflict in reader.GetConflicts())
            Assert.Equal(expected[conflict.ZoneGroupId], conflict.GetSnapshot());
    }

    [Fact]
    public void Save_RolledBackTransactionLeavesPreviousSnapshot()
    {
        var expected = new Dictionary<ushort, ZoneConflictSnapshot>
        {
            [65010] = new(ZoneConflictType.Danger, 81, DateTime.MinValue)
        };
        var manager = CreateManager(expected);
        using var connection = MySQL.CreateConnection();
        using (var transaction = connection.BeginTransaction())
        {
            manager.Save(connection, transaction);
            transaction.Commit();
        }
        manager.GetConflicts()[0].AddZoneKill();
        using (var transaction = connection.BeginTransaction())
        {
            manager.Save(connection, transaction);
            transaction.Rollback();
        }
        manager.RestoreStates(connection);
        Assert.Equal(expected[65010], manager.GetConflicts()[0].GetSnapshot());
    }

    [Fact]
    public void DoSave_NoOnlineCharactersCommitsZoneState()
    {
        var expected = new Dictionary<ushort, ZoneConflictSnapshot>
        {
            [65011] = new(ZoneConflictType.Dispute, 117, DateTime.MinValue)
        };
        var zoneManager = CreateManager(expected);
        var world = new Mock<IWorldManager>();
        world.Setup(manager => manager.GetAllCharacters()).Returns([]);
        world.Setup(manager => manager.GetWorlds()).Returns([]);
        var save = new SaveManager(Mock.Of<ITaskManager>(), Mock.Of<IHousingManager>(),
            Mock.Of<IMailManager>(), Mock.Of<IItemManager>(), Mock.Of<IAuctionManager>(),
            Mock.Of<ICrimeManager>(), world.Object, zoneManager);

        Assert.True(save.DoSave());

        using var connection = MySQL.CreateConnection();
        zoneManager.RestoreStates(connection);
        Assert.Equal(expected[65011], zoneManager.GetConflicts()[0].GetSnapshot());
    }

    private static ZoneManager CreateManager(Dictionary<ushort, ZoneConflictSnapshot> states)
    {
        var manager = new ZoneManager(Mock.Of<IWorldManager>(), Mock.Of<ITaskManager>());
        var conflicts = new Dictionary<ushort, ZoneConflict>();
        foreach (var (id, state) in states)
        {
            var conflict = new ZoneConflict(null) { ZoneGroupId = id, ConflictMin = 5, WarMin = 80, PeaceMin = 120 };
            new[] { 70, 100, 140, 190, 250 }.CopyTo(conflict.NumKills, 0);
            conflict.Restore(state);
            conflicts.Add(id, conflict);
        }
        typeof(ZoneManager).GetField("_conflicts", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, conflicts);
        return manager;
    }
}
