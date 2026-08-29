using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

[NotInParallel]
public sealed class CSNotifyInGameCompletedPacketTests
{
    private static readonly FieldInfo s_worldManagerInstanceField =
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_timeManagerInstanceField =
        typeof(Singleton<TimeManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private WorldManager _previousWorldManager;
    private TimeManager _previousTimeManager;
    private TimeManager _timeManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = (WorldManager)s_worldManagerInstanceField.GetValue(null);
        _previousTimeManager = (TimeManager)s_timeManagerInstanceField.GetValue(null);

        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        s_worldManagerInstanceField.SetValue(null, worldManager);

        var tickManager = Mock.Of<ITickManager>();
        tickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-15T18:30:00Z"));
        _timeManager = new TimeManager(
            tickManager.Object,
            worldManager,
            timeProvider,
            Options.Create(new AppConfiguration
            {
                World = new WorldConfig
                {
                    Time = new WorldTimeConfig
                    {
                        Mode = WorldTimeMode.TimeZone,
                        TimeZoneId = "America/Chicago"
                    }
                }
            }));
        _timeManager.Start();
        s_timeManagerInstanceField.SetValue(null, _timeManager);
    }

    [After(Test)]
    public void TearDown()
    {
        _timeManager.Stop();
        s_timeManagerInstanceField.SetValue(null, _previousTimeManager);
        s_worldManagerInstanceField.SetValue(null, _previousWorldManager);
    }

    [Test]
    public void Read_InitialLoginSequence_SendsCooldownSnapshotOnlyAfterInGameCompleted()
    {
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object);
        var character = new CharacterMock { Connection = connection };
        character.Cooldowns.AddCooldown(100, 30_000);
        connection.ActiveChar = character;

        var instanceLoadedPacket = new CSInstanceLoadedPacket { Connection = connection };
        instanceLoadedPacket.Read(new PacketStream());
        session.SendPacket(Is<byte[]>(IsCooldownPacket)).WasCalled(Times.Never);

        var inGameCompletedPacket = new CSNotifyInGameCompletedPacket { Connection = connection };
        inGameCompletedPacket.Read(new PacketStream());

        session.SendPacket(Is<byte[]>(IsCooldownPacket)).WasCalled(Times.Once);
    }

    [Test]
    public void Read_InstanceLoaded_SendsAuthoritativeDetailedTime()
    {
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object);
        connection.ActiveChar = new CharacterMock { Connection = connection };
        var packet = new CSInstanceLoadedPacket { Connection = connection };

        packet.Read(new PacketStream());

        session.SendPacket(Is<byte[]>(IsCentralDetailedTimePacket)).WasCalled(Times.Once);
    }

    private static bool IsCooldownPacket(byte[] packet)
    {
        return packet.Length >= 8 &&
               packet[6] == (byte)SCOffsets.SCCooldownsPacket &&
               packet[7] == (byte)(SCOffsets.SCCooldownsPacket >> 8);
    }

    private static bool IsCentralDetailedTimePacket(byte[] packet)
    {
        return packet.Length == 24 &&
               packet[6] == (byte)SCOffsets.SCDetailedTimeOfDayPacket &&
               packet[7] == (byte)(SCOffsets.SCDetailedTimeOfDayPacket >> 8) &&
               Math.Abs(BitConverter.ToSingle(packet, 8) - 12.5f) < 0.001f &&
               Math.Abs(BitConverter.ToSingle(packet, 12) - 1f / 3600f) < 0.0000001f &&
               BitConverter.ToSingle(packet, 16) == 0f &&
               BitConverter.ToSingle(packet, 20) == 24f;
    }
}
