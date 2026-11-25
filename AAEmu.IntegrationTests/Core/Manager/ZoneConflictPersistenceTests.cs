using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World.Zones;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShutdownTask = AAEmu.Game.Models.Tasks.ShutdownTask;

using Moq;
using MySql.Data.MySqlClient;
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

    [Theory]
    [InlineData(0, false, 1, true)]
    [InlineData(1, false, 2, true)]
    [InlineData(2, false, 2, false)]
    [InlineData(0, true, 1, false)]
    public async Task Shutdown_CheckpointsZoneStateOrReportsFailure(
        int preCommitFailures, bool uncertainCommit, int expectedAttempts, bool cleanShutdown)
    {
        var id = (ushort)(65100 + preCommitFailures + (uncertainCommit ? 10 : 0));
        var oldState = new ZoneConflictSnapshot(ZoneConflictType.Danger, 80, DateTime.MinValue);
        var zone = CreateManager(new Dictionary<ushort, ZoneConflictSnapshot> { [id] = oldState });
        using (var connection = MySQL.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            zone.Save(connection, transaction);
            transaction.Commit();
        }
        zone.GetConflicts()[0].AddZoneKill();
        var newState = zone.GetConflicts()[0].GetSnapshot();
        Assert.NotEqual(oldState, newState);

        var zoneWrites = new Mock<IZoneManager>();
        var scheduler = new TaskManager(Mock.Of<ITickManager>(tick => tick.OnTick == new TickManager.TickEventHandler()));
        var graph = new EconomyPersistenceTests.SaveGraph(scheduler, zoneWrites.Object);
        var item = graph.AddItem(1);
        var mail = graph.AddMail(2);
        var dirtyBeforeCommit = new List<bool>();
        var writes = 0;
        zoneWrites.Setup(manager => manager.Save(It.IsAny<MySqlConnection>(), It.IsAny<MySqlTransaction>()))
            .Returns((MySqlConnection connection, MySqlTransaction transaction) =>
            {
                dirtyBeforeCommit.Add(item.IsDirty && mail.IsDirty && graph.Container.IsDirty);
                var rows = zone.Save(connection, transaction);
                if (++writes <= preCommitFailures)
                    throw new InvalidOperationException("Simulated failure after the zone write and before commit.");
                return rows;
            });
        var save = graph.Save;
        var consistencyStops = 0;
        save.StopForConsistencyFailure = (_, _) => consistencyStops++;
        if (uncertainCommit)
            save.CommitTransaction = transaction =>
            {
                transaction.Commit();
                throw new InvalidOperationException("Simulated loss of commit confirmation.");
            };

        // The production cancellation uses TaskManager.Instance on the saved scheduled task.
        var schedulerField = typeof(Singleton<TaskManager>).GetField("s_instance",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousScheduler = schedulerField.GetValue(null);
        schedulerField.SetValue(null, scheduler);
        Exception failure;
        try
        {
            save.SaveTickStart();
            failure = await StopFromStaffCountdown(scheduler, save);
        }
        finally
        {
            schedulerField.SetValue(null, previousScheduler);
        }

        using var reader = MySQL.CreateConnection();
        zone.RestoreStates(reader);
        Assert.Equal(cleanShutdown || uncertainCommit ? newState : oldState, zone.GetConflicts()[0].GetSnapshot());
        Assert.Equal(expectedAttempts, writes);
        Assert.All(dirtyBeforeCommit, Assert.True);
        Assert.Equal(!cleanShutdown, item.IsDirty);
        Assert.Equal(!cleanShutdown, mail.IsDirty);
        Assert.Equal(!cleanShutdown, graph.Container.IsDirty);
        using var query = reader.CreateCommand();
        query.CommandText = "SELECT (SELECT COUNT(*) FROM items WHERE id=@item), (SELECT COUNT(*) FROM mails WHERE id=@mail)";
        query.Parameters.AddWithValue("@item", item.Id);
        query.Parameters.AddWithValue("@mail", mail.Id);
        using var rows = query.ExecuteReader();
        Assert.True(rows.Read());
        var savedRows = cleanShutdown || uncertainCommit ? 1L : 0L;
        Assert.Equal(savedRows, rows.GetInt64(0));
        Assert.Equal(savedRows, rows.GetInt64(1));
        Assert.Equal(uncertainCommit ? 1 : 0, consistencyStops);
        if (cleanShutdown)
            Assert.Null(failure);
        else
            Assert.IsType<InvalidOperationException>(failure);
    }

    private static async Task<Exception> StopFromStaffCountdown(TaskManager scheduler, SaveManager save)
    {
        var hosted = new Mock<IHostedService>();
        hosted.Setup(service => service.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        hosted.Setup(service => service.StopAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            // GameService owns this order. The countdown callback must return before the scheduler can drain.
            scheduler.Stop();
            return save.StopAsync();
        });
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddSingleton(hosted.Object))
            .Build();
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var world = new Mock<IWorldManager>();
        world.Setup(manager => manager.GetAllCharacters()).Returns([]);
        var cancelled = new ShutdownTask(DateTime.UtcNow.AddSeconds(-1), world.Object, lifetime);
        Assert.True(scheduler.Schedule(cancelled, TimeSpan.FromSeconds(1)));
        Assert.True(cancelled.CancelCountdown());
        cancelled.Execute(); // A callback that the scheduler already collected must respect cancellation.
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        var countdown = new ShutdownTask(DateTime.UtcNow.AddMinutes(5), world.Object, lifetime);
        var completion = host.WaitForShutdownAsync();
        countdown.Execute();
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        Assert.True(countdown.ChangeShutdownTime(DateTime.UtcNow.AddSeconds(-1)));
        Assert.True(scheduler.Schedule(countdown, TimeSpan.Zero, count: 1));
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.False(countdown.ChangeShutdownTime(DateTime.UtcNow.AddMinutes(5)));
        countdown.Execute();
        var failure = await Record.ExceptionAsync(() => completion.WaitAsync(TimeSpan.FromSeconds(15)));
        world.Verify(manager => manager.GetAllCharacters(), Times.Once);
        hosted.Verify(service => service.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        return failure;
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
