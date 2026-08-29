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
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

[NotInParallel]
public sealed class CSNotifyInGameCompletedPacketTests
{
    private static readonly FieldInfo s_worldManagerInstanceField =
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private WorldManager _previousWorldManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = (WorldManager)s_worldManagerInstanceField.GetValue(null);

        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        s_worldManagerInstanceField.SetValue(null, worldManager);
    }

    [After(Test)]
    public void TearDown()
    {
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

    private static bool IsCooldownPacket(byte[] packet)
    {
        return packet.Length >= 8 &&
               packet[6] == (byte)SCOffsets.SCCooldownsPacket &&
               packet[7] == (byte)(SCOffsets.SCCooldownsPacket >> 8);
    }
}
