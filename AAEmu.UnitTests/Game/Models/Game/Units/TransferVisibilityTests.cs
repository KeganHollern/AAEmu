using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public class TransferVisibilityTests
{
    [Test]
    public async Task AddToCharacters_CarriageWithSeats_CreatesSeatsOnceAfterTransferStates()
    {
        var motor = CreateTransfer(100, 6, AttachPointKind.System, 0);
        var carriage = CreateTransfer(101, 46, AttachPointKind.Trailed0, motor.ObjId);
        motor.Bounded = carriage;
        var firstSeat = CreateSeat(102, carriage, AttachPointKind.Passenger0);
        var secondSeat = CreateSeat(103, carriage, AttachPointKind.Passenger1);
        var session = new RecordingSession();
        var viewer = new CharacterMock
        {
            ObjId = 104,
            Connection = new GameConnection(session)
        };
        var region = new Region(null, 0, 0, 0);
        SetRegionObjects(region, motor, carriage, firstSeat, secondSeat, viewer);

        region.AddToCharacters(viewer);

        ushort[] expectedOpcodes =
        [
            SCOffsets.SCUnitStatePacket,
            SCOffsets.SCUnitStatePacket,
            SCOffsets.SCDoodadsCreatedPacket
        ];
        await Assert.That(session.Packets.Count).IsEqualTo(expectedOpcodes.Length);
        for (var index = 0; index < expectedOpcodes.Length; index++)
            await Assert.That(BitConverter.ToUInt16(session.Packets[index], 6))
                .IsEqualTo(expectedOpcodes[index]);
        await Assert.That(session.Packets[2][8]).IsEqualTo((byte)2);
    }

    private static Transfer CreateTransfer(uint objId, uint templateId, AttachPointKind attachPoint, uint bondingObjId)
    {
        return new Transfer
        {
            ObjId = objId,
            TlId = 1,
            Name = $"Transfer {templateId}",
            TemplateId = templateId,
            ModelId = templateId,
            AttachPointId = attachPoint,
            BondingObjId = bondingObjId,
            Level = 1
        };
    }

    private static Doodad CreateSeat(uint objId, Transfer parent, AttachPointKind attachPoint)
    {
        var seat = new Doodad
        {
            ObjId = objId,
            TemplateId = 5890,
            ParentObjId = parent.ObjId,
            AttachPoint = attachPoint
        };
        seat.Transform.Parent = parent.Transform;
        parent.AttachedDoodads.Add(seat);
        return seat;
    }

    private static void SetRegionObjects(Region region, params GameObject[] objects)
    {
        var regionType = typeof(Region);
        regionType.GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(region, objects);
        regionType.GetField("_objectsSize", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(region, objects.Length);
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];

        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;

        public void SendPacket(byte[] packet)
        {
            Packets.Add(packet.ToArray());
        }

        public void AddAttribute(string name, object attribute)
        {
            _attributes.Add(name, attribute);
        }

        public object GetAttribute(string name)
        {
            return _attributes.GetValueOrDefault(name);
        }

        public void ClearAttribute(string name)
        {
            _attributes.Remove(name);
        }

        public void Close()
        {
        }
    }
}
