using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Observers;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Observers;

public class TimeOfDayObserverTests
{
    [Test]
    public void OnNext_SendsExactDetailedClockSnapshot()
    {
        const float time = 15.75f;
        const float speed = 1f / 3600f;
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object);
        var character = new CharacterMock { Connection = connection };
        var observer = new TimeOfDayObserver(character, speed);

        observer.OnNext(time);

        session.SendPacket(Is<byte[]>(packet => IsDetailedClockPacket(packet, time, speed)))
            .WasCalled(Times.Once);
        session.SendPacket(Is<byte[]>(IsSimpleClockPacket)).WasCalled(Times.Never);
    }

    private static bool IsDetailedClockPacket(byte[] packet, float time, float speed)
    {
        return packet.Length == 24 &&
               packet[6] == (byte)SCOffsets.SCDetailedTimeOfDayPacket &&
               packet[7] == (byte)(SCOffsets.SCDetailedTimeOfDayPacket >> 8) &&
               BitConverter.ToSingle(packet, 8) == time &&
               BitConverter.ToSingle(packet, 12) == speed &&
               BitConverter.ToSingle(packet, 16) == 0f &&
               BitConverter.ToSingle(packet, 20) == 24f;
    }

    private static bool IsSimpleClockPacket(byte[] packet)
    {
        return packet.Length >= 8 &&
               packet[6] == (byte)SCOffsets.SCTimeOfDayPacket &&
               packet[7] == (byte)(SCOffsets.SCTimeOfDayPacket >> 8);
    }
}
