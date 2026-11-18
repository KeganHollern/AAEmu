using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

[NotInParallel]
public sealed class CSStartInteractionPacketTests
{
    private static readonly FieldInfo s_worldInstance = typeof(Singleton<WorldManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previous;
    private CharacterMock _character;
    private Npc _npc;
    private RecordingSession _session;

    [Before(Test)]
    public void SetUp()
    {
        _previous = s_worldInstance.GetValue(null);
        var manager = new WorldManager(null, null, null, null, null);
        s_worldInstance.SetValue(null, manager);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        ((ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!)[1] = world;
        _session = new RecordingSession();
        var connection = new GameConnection(_session);
        _character = new CharacterMock
        {
            Id = 7, ObjId = 70, Level = 50, ParentWorld = world,
            Money = 100, Money2 = 100, Connection = connection
        };
        connection.ActiveChar = _character;
        _npc = new Npc
        {
            ObjId = 80, ParentWorld = world,
            Template = new NpcTemplate { Banker = true }
        };
        world.AddObject(_npc);
    }

    [After(Test)]
    public void TearDown() => s_worldInstance.SetValue(null, _previous);

    [Test]
    [Arguments(0f)]
    [Arguments(5f)]
    [Arguments(1000f)]
    public async Task Discovery_BeforeApproach_OnlyReturnsTheActionAndEchoedContext(float distance)
    {
        _npc.Transform.Local.SetPosition(distance, 0, 0);
        var request = Request();
        new CSStartInteractionPacket { Connection = _character.Connection }.Read(request);

        await Assert.That(request.LeftBytes).IsEqualTo(0);
        await Assert.That(_session.Packets).HasSingleItem();
        await Assert.That(BitConverter.ToUInt16(_session.Packets[0], 6))
            .IsEqualTo(SCOffsets.SCNpcInteractionSkillListPacket);
        var response = new PacketStream(_session.Packets[0], 8, _session.Packets[0].Length - 8);
        await Assert.That(response.ReadBc()).IsEqualTo(_npc.ObjId);
        await Assert.That(response.ReadBc()).IsEqualTo(_character.ObjId);
        await Assert.That(response.ReadInt32()).IsEqualTo(17);
        await Assert.That(response.ReadInt32()).IsEqualTo(29);
        await Assert.That(response.ReadByte()).IsEqualTo((byte)1);
        await Assert.That(response.ReadInt32()).IsEqualTo(1);
        await Assert.That(response.ReadUInt32()).IsEqualTo((uint)SkillsEnum.UseWarehouse);
        await Assert.That(response.ReadByte()).IsEqualTo((byte)1);
        await Assert.That(response.ReadInt32()).IsEqualTo(3);
        await Assert.That(response.LeftBytes).IsEqualTo(0);
        await Assert.That(_character.CurrentInteractionObject).IsNull();
        await Assert.That(_character.CurrentTarget).IsNull();
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_character.Money2).IsEqualTo(100L);
    }

    [Test]
    [Arguments(1000f, 0f)]
    [Arguments(0f, 5.01f)]
    public async Task Discovery_DistantNpc_CannotOpenAnInteractionOrTransferMoney(float x, float z)
    {
        _npc.Transform.Local.SetPosition(x, 0, z);
        new CSStartInteractionPacket { Connection = _character.Connection }.Read(Request());
        new CSInteractNPCPacket { Connection = _character.Connection }
            .Read(new PacketStream().WriteBc(_npc.ObjId).Write(true));
        await Assert.That(_session.Packets).HasSingleItem();
        new CSDepositMoneyPacket { Connection = _character.Connection }
            .Read(new PacketStream().Write(100).Write(0));
        new CSWithdrawMoneyPacket { Connection = _character.Connection }
            .Read(new PacketStream().Write(100).Write(0));

        await Assert.That(_session.Packets.Count).IsEqualTo(3);
        foreach (var packet in _session.Packets.Skip(1))
        {
            await Assert.That(BitConverter.ToUInt16(packet, 6)).IsEqualTo(SCOffsets.SCErrorMsgPacket);
            await Assert.That(BitConverter.ToInt16(packet, 8)).IsEqualTo((short)ErrorMessageType.NoInteractionAvailable);
        }
        await Assert.That(_character.CurrentInteractionObject).IsNull();
        await Assert.That(_character.CurrentTarget).IsNull();
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_character.Money2).IsEqualTo(100L);
    }

    [Test]
    public async Task Discovery_ThenApproach_OpensTheSameNpcAndAllowsNormalBankUse()
    {
        _npc.Transform.Local.SetPosition(1000, 0, 0);
        new CSStartInteractionPacket { Connection = _character.Connection }.Read(Request());
        _npc.Transform.Local.SetPosition(3, 4, 0);
        new CSInteractNPCPacket { Connection = _character.Connection }
            .Read(new PacketStream().WriteBc(_npc.ObjId).Write(true));
        new CSDepositMoneyPacket { Connection = _character.Connection }
            .Read(new PacketStream().Write(100).Write(0));

        await Assert.That(_character.CurrentInteractionObject).IsSameReferenceAs(_npc);
        await Assert.That(_character.CurrentTarget).IsSameReferenceAs(_npc);
        await Assert.That(_character.Money).IsEqualTo(0L);
        await Assert.That(_character.Money2).IsEqualTo(200L);
    }

    [Test]
    public async Task Discovery_UnknownNpc_DoesNotReturnAnAction()
    {
        new CSStartInteractionPacket { Connection = _character.Connection }.Read(Request(999));
        await Assert.That(_session.Packets).IsEmpty();
        await Assert.That(_character.CurrentInteractionObject).IsNull();
    }

    [Test]
    public async Task Discovery_TruncatedRequest_DoesNotSendAResponseOrChangeState()
    {
        var bytes = Request().GetBytes();
        await Assert.That(bytes.Length).IsEqualTo(19);
        for (var length = 0; length < bytes.Length; length++)
        {
            var packet = new CSStartInteractionPacket { Connection = _character.Connection };
            Assert.Throws<MarshalException>(() => packet.Read(new PacketStream(bytes, 0, length)));
            await Assert.That(_session.Packets).IsEmpty();
            await Assert.That(_character.CurrentInteractionObject).IsNull();
            await Assert.That(_character.Money).IsEqualTo(100L);
        }
    }

    private PacketStream Request(uint npcId = 80) => new PacketStream()
        .WriteBc(npcId).WriteBc(_character.ObjId).Write(17).Write(29).Write((byte)1).Write(3);

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
