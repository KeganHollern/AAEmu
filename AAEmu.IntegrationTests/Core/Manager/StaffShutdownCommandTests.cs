using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Core.Packets.G2C;
using ShutdownTask = AAEmu.Game.Models.Tasks.ShutdownTask;
using Microsoft.Extensions.Hosting;
using MySql.Data.MySqlClient;
using AAEmu.Game.Utils.Scripts;
using Moq;
using Xunit;
using GameTask = AAEmu.Game.Models.Tasks.Task;
using StaffCommand = AAEmu.Game.Scripts.Commands.Scripts;
using Task = System.Threading.Tasks.Task;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class StaffShutdownCommandTests
{
    [Fact]
    public async Task TwoStaffRequestsShareOneCountdownAndLateCancellationCannotClearItsReplacement()
    {
        var tick = Mock.Of<ITickManager>(t => t.OnTick == new TickManager.TickEventHandler());
        var scheduler = new TaskManager(tick);
        var scheduled = new ConcurrentQueue<GameTask>();
        var tasks = new Mock<ITaskManager>();
        tasks.Setup(m => m.Schedule(It.IsAny<GameTask>(), null, TimeSpan.FromSeconds(1), -1))
            .Returns((GameTask task, TimeSpan? start, TimeSpan? repeat, int count) =>
            {
                if (!scheduler.Schedule(task, start, repeat, count))
                    return false;
                scheduled.Enqueue(task);
                return true;
            });
        using var cancelEntered = new ManualResetEventSlim();
        using var finishCancel = new ManualResetEventSlim();
        tasks.Setup(m => m.Cancel(It.IsAny<GameTask>())).Returns((GameTask task) =>
        {
            cancelEntered.Set();
            Assert.True(finishCancel.Wait(TimeSpan.FromSeconds(15)));
            return scheduler.Cancel(task);
        });
        var zones = new Mock<IZoneManager>();
        var saveCount = 0;
        zones.Setup(m => m.Save(It.IsAny<MySqlConnection>(), It.IsAny<MySqlTransaction>()))
            .Returns(() => Interlocked.Increment(ref saveCount) == 1 ? throw new InvalidOperationException("Preflight failed.") : 0);
        var lifetime = new Mock<IHostApplicationLifetime>();
        var save = new EconomyPersistenceTests.SaveGraph(tasks.Object, zones.Object, lifetime.Object).Save;
        var oldSave = SwapSingleton(save);
        var oldWorld = SwapSingleton(new WorldManager(null, null, null, null, null));
        try
        {
            Assert.Contains("save errors", Command(1, "5"));
            Assert.Empty(scheduled);
            var starts = await Task.WhenAll(Task.Run(() => Command(1, "5")), Task.Run(() => Command(2, "10")));
            Assert.Single(starts, text => text.Contains("sequence started", StringComparison.Ordinal));
            Assert.Single(starts, text => text.Contains("already in progress", StringComparison.Ordinal));
            var original = Assert.Single(scheduled);

            var cancel = Task.Run(() => Command(1, "cancel"));
            Assert.True(cancelEntered.Wait(TimeSpan.FromSeconds(15)));
            using var startRequested = new ManualResetEventSlim();
            var replacement = Task.Run(() =>
            {
                startRequested.Set();
                return Command(2, "5");
            });
            Assert.True(startRequested.Wait(TimeSpan.FromSeconds(15)));
            finishCancel.Set();
            Assert.Contains("cancelled", await cancel);
            Assert.Contains("sequence started", await replacement);
            Assert.Equal(2, scheduled.Count);
            Assert.True(original.Cancelled);
            original.Execute();
            Assert.Contains("already in progress", Command(1, "10"));
            Assert.Contains("cancelled", Command(2, "cancel"));
            Assert.Contains("No shutdown", Command(1, "cancel"));

            scheduler.Stop();
            Assert.Contains("already stopping", Command(1, "5"));
            Assert.Contains("No shutdown", Command(2, "cancel"));
            Assert.Equal(2, scheduled.Count);
            lifetime.Verify(m => m.StopApplication(), Times.Never);
        }
        finally
        {
            finishCancel.Set();
            SwapSingleton(oldWorld);
            SwapSingleton(oldSave);
        }
    }

    [Fact]
    public async Task OccupiedWorldShowsOneFinalNoticeBeforeTheHostStops()
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        var world = new Mock<IWorldManager>();
        world.Setup(m => m.GetAllCharacters()).Returns([new Character(null)]);
        var shutdown = new ShutdownTask(DateTime.UtcNow.AddSeconds(-1), world.Object, lifetime.Object);

        var pending = shutdown.ExecuteAsync();
        world.Verify(m => m.BroadcastPacketToServer(It.Is<SCNoticeMessagePacket>(packet => IsImmediateNotice(packet))), Times.Once);
        Assert.False(shutdown.ChangeShutdownTime(DateTime.UtcNow.AddMinutes(10)));
        await shutdown.ExecuteAsync(); // A second scheduler tick while the player sees the first notice.
        await Task.Delay(250);
        lifetime.Verify(m => m.StopApplication(), Times.Never);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        world.Verify(m => m.BroadcastPacketToServer(It.Is<SCNoticeMessagePacket>(packet => IsImmediateNotice(packet))), Times.Once);
        lifetime.Verify(m => m.StopApplication(), Times.Once);
    }

    [Fact]
    public async Task AnIndependentHostStopEndsTheFinalNoticeInterval()
    {
        using var stopping = new CancellationTokenSource();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(m => m.ApplicationStopping).Returns(stopping.Token);
        var world = new Mock<IWorldManager>();
        world.Setup(m => m.GetAllCharacters()).Returns([new Character(null)]);
        var shutdown = new ShutdownTask(DateTime.UtcNow.AddSeconds(-1), world.Object, lifetime.Object);

        var pending = shutdown.ExecuteAsync();
        Assert.False(pending.IsCompleted);
        await stopping.CancelAsync();
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
        lifetime.Verify(m => m.StopApplication(), Times.Once);
    }

    private static bool IsImmediateNotice(SCNoticeMessagePacket packet) => packet.Write(new PacketStream()).ReadByte() == 1;

    private static string Command(uint characterId, string argument)
    {
        var output = new Mock<IMessageOutput>();
        new StaffCommand().Execute(new Character(null) { Id = characterId }, ["shutdown", argument], output.Object);
        return (string)Assert.Single(output.Invocations).Arguments[1];
    }

    private static T SwapSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (T)field.GetValue(null);
        field.SetValue(null, value);
        return previous;
    }
}
