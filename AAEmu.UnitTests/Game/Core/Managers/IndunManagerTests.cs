using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class IndunManagerTests
{
    [Test]
    public void Initialize_SubscribesToTickManager()
    {
        var mockTick = Mock.Of<ITickManager>();
        mockTick.OnTick.Returns(new TickManager.TickEventHandler());
        var manager = CreateManager(new FakeTimeProvider(), null, mockTick.Object);
        manager.Initialize();

        mockTick.OnTick.WasCalled(Times.Once);
    }

    [Test]
    public async Task TryReserveDungeonCreation_AtConfiguredLimit_RejectsNextCreation()
    {
        var manager = CreateManager(new FakeTimeProvider());

        var first = manager.TryReserveDungeonCreation(1, 50, out _);
        var second = manager.TryReserveDungeonCreation(1, 50, out _);
        var third = manager.TryReserveDungeonCreation(1, 50, out _);
        var fourth = manager.TryReserveDungeonCreation(1, 50, out _);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(third).IsTrue();
        await Assert.That(fourth).IsFalse();
        await Assert.That(manager.GetRecentDungeonCreationCount(1, 50)).IsEqualTo(3);
    }

    [Test]
    public async Task TryReserveDungeonCreation_DifferentDungeonZone_HasSeparateLimit()
    {
        var manager = CreateManager(new FakeTimeProvider(), new DungeonsConfig
        {
            CreationLimit = 1,
            CreationWindowMinutes = 15
        });

        var firstZone = manager.TryReserveDungeonCreation(1, 50, out _);
        var secondZone = manager.TryReserveDungeonCreation(1, 51, out _);

        await Assert.That(firstZone).IsTrue();
        await Assert.That(secondZone).IsTrue();
    }

    [Test]
    public async Task TryReserveDungeonCreation_AfterWindowExpires_AllowsCreation()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(timeProvider, new DungeonsConfig
        {
            CreationLimit = 1,
            CreationWindowMinutes = 15
        });

        manager.TryReserveDungeonCreation(1, 50, out _);
        timeProvider.Advance(TimeSpan.FromMinutes(15));

        var result = manager.TryReserveDungeonCreation(1, 50, out _);

        await Assert.That(result).IsTrue();
        await Assert.That(manager.GetRecentDungeonCreationCount(1, 50)).IsEqualTo(1);
    }

    [Test]
    public async Task ReleaseDungeonCreationReservation_FailedCreation_DoesNotConsumeLimit()
    {
        var manager = CreateManager(new FakeTimeProvider(), new DungeonsConfig
        {
            CreationLimit = 1,
            CreationWindowMinutes = 15
        });

        manager.TryReserveDungeonCreation(1, 50, out var reservationTime);
        manager.ReleaseDungeonCreationReservation(1, 50, reservationTime);

        var result = manager.TryReserveDungeonCreation(1, 50, out _);

        await Assert.That(result).IsTrue();
        await Assert.That(manager.GetRecentDungeonCreationCount(1, 50)).IsEqualTo(1);
    }

    [Test]
    public async Task TryReserveDungeonCreation_ThrottleDisabled_AllowsUnlimitedCreations()
    {
        var manager = CreateManager(new FakeTimeProvider(), new DungeonsConfig
        {
            CreationLimit = 0,
            CreationWindowMinutes = 15
        });

        for (var i = 0; i < 10; i++)
        {
            await Assert.That(manager.TryReserveDungeonCreation(1, 50, out _)).IsTrue();
        }

        await Assert.That(manager.GetRecentDungeonCreationCount(1, 50)).IsEqualTo(0);
    }

    private static IndunManager CreateManager(
        FakeTimeProvider timeProvider,
        DungeonsConfig dungeonConfig = null,
        ITickManager tickManager = null)
    {
        tickManager ??= Mock.Of<ITickManager>().Object;
        var configuration = Options.Create(new AppConfiguration
        {
            Dungeons = dungeonConfig ?? new DungeonsConfig()
        });

        return new IndunManager(
            tickManager,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IZoneManager>().Object,
            Mock.Of<ITeamManager>().Object,
            timeProvider,
            configuration);
    }
}
