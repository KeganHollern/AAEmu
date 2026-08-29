using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class TimeManagerTests
{
    private const float RealTimeClientSpeed = 1f / 3600f;

    [Test]
    [Arguments("2026-01-15T18:30:00Z")]
    [Arguments("2026-07-15T17:30:00Z")]
    public async Task TimeZoneMode_Chicago_UsesCentralWallClock(string utcNow)
    {
        var timeProvider = CreateTimeProvider(utcNow);
        var (manager, _, _) = CreateManager(timeProvider, CreateTimeZoneConfiguration());

        manager.Start();

        await Assert.That(manager.GetTime).IsEqualTo(12.5f).Within(0.001f);
        await Assert.That(manager.ClientSpeed).IsEqualTo(RealTimeClientSpeed).Within(0.0000001f);
    }

    [Test]
    public async Task TimeZoneMode_NewManagerAtSameInstant_RestoresTimeWithoutStoredState()
    {
        var timeProvider = CreateTimeProvider("2026-08-29T18:42:30Z");
        var configuration = CreateTimeZoneConfiguration();
        var (first, _, _) = CreateManager(timeProvider, configuration);
        var (second, _, _) = CreateManager(timeProvider, configuration);

        first.Start();
        second.Start();

        await Assert.That(second.GetTime).IsEqualTo(first.GetTime).Within(0.0001f);
    }

    [Test]
    public async Task TimeZoneMode_DelayedTick_UsesAuthoritativeClock()
    {
        var timeProvider = CreateTimeProvider("2026-01-15T18:30:00Z");
        var (manager, _, _) = CreateManager(timeProvider, CreateTimeZoneConfiguration());
        manager.Start();

        timeProvider.Advance(TimeSpan.FromMinutes(17));
        manager.Update(default);

        await Assert.That(manager.GetTime).IsEqualTo(12f + 47f / 60f).Within(0.001f);
    }

    [Test]
    public async Task AcceleratedMode_ConfiguredDayLength_DerivesEpochPhaseAndClientSpeed()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var configuration = new WorldTimeConfig
        {
            Mode = WorldTimeMode.Accelerated,
            AcceleratedDayLengthMinutes = 240d
        };
        var (manager, _, _) = CreateManager(timeProvider, configuration);
        manager.Start();

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        manager.Update(default);

        await Assert.That(manager.GetTime).IsEqualTo(1f / 60f).Within(0.0001f);
        await Assert.That(manager.ClientSpeed).IsEqualTo(1f / 600f).Within(0.0000001f);
    }

    [Test]
    public async Task TimeZoneMode_Set_ReturnsFalseAndKeepsWallClock()
    {
        var timeProvider = CreateTimeProvider("2026-01-15T18:30:00Z");
        var (manager, _, _) = CreateManager(timeProvider, CreateTimeZoneConfiguration());
        manager.Start();

        var result = manager.Set(4f);

        await Assert.That(result).IsFalse();
        await Assert.That(manager.GetTime).IsEqualTo(12.5f).Within(0.001f);
    }

    [Test]
    public async Task TimeZoneMode_SpringTransition_AdvancesToThreeAndProcessesWorldEffects()
    {
        var timeProvider = CreateTimeProvider("2026-03-08T07:59:50Z");
        var (manager, worldManager, _) = CreateManager(timeProvider, CreateTimeZoneConfiguration());
        manager.Start();

        timeProvider.Advance(TimeSpan.FromSeconds(20));
        manager.Update(default);

        await Assert.That(manager.GetTime).IsEqualTo(3f + 10f / 3600f).Within(0.001f);
        worldManager.GetWorlds().WasCalled(Times.Once);
    }

    [Test]
    public async Task TimeZoneMode_FallTransition_DoesNotRunRepeatedWorldEffects()
    {
        var timeProvider = CreateTimeProvider("2026-11-01T06:59:50Z");
        var (manager, worldManager, _) = CreateManager(timeProvider, CreateTimeZoneConfiguration());
        manager.Start();

        timeProvider.Advance(TimeSpan.FromSeconds(20));
        manager.Update(default);
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        manager.Update(default);

        worldManager.GetWorlds().WasCalled(Times.Never);

        timeProvider.Advance(TimeSpan.FromMinutes(30));
        manager.Update(default);

        await Assert.That(manager.GetTime).IsEqualTo(2f + 10f / 3600f).Within(0.001f);
        worldManager.GetWorlds().WasCalled(Times.Once);
    }

    [Test]
    public async Task TimeZoneMode_RestartDuringRepeatedHour_SuppressesWorldEffectsUntilTwo()
    {
        var timeProvider = CreateTimeProvider("2026-11-01T07:30:00Z");
        var (manager, worldManager, _) = CreateManager(timeProvider, CreateTimeZoneConfiguration());
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var npc = new TimeEffectProbeNpc
        {
            ObjId = 1,
            Template = new NpcTemplate
            {
                NpcPostureSets =
                [
                    new NpcPosture { StartTodTime = 2f, AnimActionId = 2 },
                    new NpcPosture { StartTodTime = 0f, AnimActionId = 1 }
                ]
            }
        };
        world.AddObject(npc);
        worldManager.GetWorlds().Returns([world]);
        manager.Start();

        timeProvider.Advance(TimeSpan.FromMinutes(10));
        manager.Update(default);

        worldManager.GetWorlds().WasCalled(Times.Never);

        timeProvider.Advance(TimeSpan.FromMinutes(20).Add(TimeSpan.FromSeconds(10)));
        manager.Update(default);

        worldManager.GetWorlds().WasCalled(Times.Once);
        await Assert.That(npc.PostureChangeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ConfigurationChangeAfterStart_DoesNotChangeClockUntilRestart()
    {
        var timeProvider = CreateTimeProvider("2026-01-15T18:30:00Z");
        var configuration = CreateTimeZoneConfiguration();
        var (manager, _, _) = CreateManager(timeProvider, configuration);
        manager.Start();

        configuration.Mode = WorldTimeMode.Accelerated;
        configuration.AcceleratedDayLengthMinutes = 1d;

        await Assert.That(manager.GetTime).IsEqualTo(12.5f).Within(0.001f);
        await Assert.That(manager.ClientSpeed).IsEqualTo(RealTimeClientSpeed).Within(0.0000001f);
    }

    [Test]
    public void AcceleratedMode_BackwardUtcCorrection_DoesNotRunRepeatedWorldEffects()
    {
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1));
        var configuration = new WorldTimeConfig
        {
            Mode = WorldTimeMode.Accelerated,
            AcceleratedDayLengthMinutes = 240d
        };
        var (manager, worldManager, _) = CreateManager(timeProvider, configuration);
        manager.Start();

        timeProvider.UtcNow = timeProvider.GetUtcNow().AddSeconds(-10);
        manager.Update(default);

        worldManager.GetWorlds().WasCalled(Times.Never);

        timeProvider.UtcNow = timeProvider.GetUtcNow().AddSeconds(20);
        manager.Update(default);

        worldManager.GetWorlds().WasCalled(Times.Once);
    }

    [Test]
    public void TimeZoneMode_BackwardCorrectionAcrossMidnight_DoesNotRunRepeatedWorldEffects()
    {
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-01-15T06:00:10Z"));
        var (manager, worldManager, _) = CreateManager(timeProvider, CreateTimeZoneConfiguration());
        manager.Start();

        timeProvider.UtcNow = timeProvider.GetUtcNow().AddSeconds(-20);
        manager.Update(default);

        worldManager.GetWorlds().WasCalled(Times.Never);

        timeProvider.UtcNow = timeProvider.GetUtcNow().AddSeconds(30);
        manager.Update(default);

        worldManager.GetWorlds().WasCalled(Times.Once);
    }

    [Test]
    public async Task Start_UnknownTimeZone_ThrowsConfigurationError()
    {
        var configuration = new WorldTimeConfig
        {
            Mode = WorldTimeMode.TimeZone,
            TimeZoneId = "AAEmu/Unknown-Time-Zone"
        };
        var (manager, _, _) = CreateManager(new FakeTimeProvider(), configuration);

        var exception = Assert.Throws<InvalidOperationException>(manager.Start);

        await Assert.That(exception.Message).Contains("World.Time.TimeZoneId");
    }

    [Test]
    public async Task Start_NonPositiveAcceleratedDayLength_ThrowsConfigurationError()
    {
        var configuration = new WorldTimeConfig
        {
            Mode = WorldTimeMode.Accelerated,
            AcceleratedDayLengthMinutes = 0d
        };
        var (manager, _, _) = CreateManager(new FakeTimeProvider(), configuration);

        var exception = Assert.Throws<InvalidOperationException>(manager.Start);

        await Assert.That(exception.Message).Contains("World.Time.AcceleratedDayLengthMinutes");
    }

    private static FakeTimeProvider CreateTimeProvider(string utcNow)
    {
        return new FakeTimeProvider(DateTimeOffset.Parse(utcNow));
    }

    private static WorldTimeConfig CreateTimeZoneConfiguration()
    {
        return new WorldTimeConfig
        {
            Mode = WorldTimeMode.TimeZone,
            TimeZoneId = "America/Chicago"
        };
    }

    private static (TimeManager Manager, Mock<IWorldManager> WorldManager, Mock<ITickManager> TickManager)
        CreateManager(TimeProvider timeProvider, WorldTimeConfig timeConfiguration)
    {
        var tickManager = Mock.Of<ITickManager>();
        tickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var worldManager = Mock.Of<IWorldManager>();
        worldManager.GetWorlds().Returns([]);
        var options = Options.Create(new AppConfiguration
        {
            World = new WorldConfig { Time = timeConfiguration }
        });

        return (
            new TimeManager(tickManager.Object, worldManager.Object, timeProvider, options),
            worldManager,
            tickManager);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }

    private sealed class TimeEffectProbeNpc : Npc
    {
        public int PostureChangeCount { get; private set; }

        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            if (packet is SCUnitModelPostureChangedPacket)
                PostureChangeCount++;
        }
    }
}
