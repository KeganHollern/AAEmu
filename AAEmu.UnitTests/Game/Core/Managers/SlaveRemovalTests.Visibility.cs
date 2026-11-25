using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class SlaveRemovalTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task EnterVisibleRegion_IdentifiesOnlyTheCharacterOwnedHull(bool isOwner)
    {
        var scroll = SetSavedSummonLocation();
        _owner.Name = "Captain";
        _slave.Name = "Merchant Schooner";
        _slave.OwnerId = _owner.Id;
        var cannon = NewSlave(21, 31, BaseUnitType.Slave);
        cannon.Name = "Cannon";
        cannon.ParentObj = _slave;
        cannon.OwnerId = _slave.Id;
        cannon.Id = _slave.Id + 1;
        // A different character's saved ID can equal the owner's runtime object ID.
        var observer = new RecordingCharacter
        {
            Id = isOwner ? _owner.Id : _owner.ObjId, ObjId = 40, ParentWorld = _world,
            Connection = new GameConnection(_session)
        };
        observer.Connection.ActiveChar = observer;

        _slave.Region.AddToCharacters(observer);

        var states = _session.Packets.Where(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCSlaveStatePacket)
            .Select(ReadVisibleVehicle).ToArray();
        await Assert.That(states.Length).IsEqualTo(2);
        await Assert.That(states.Single(state => state.ObjectId == _slave.ObjId))
            .IsEqualTo((_slave.ObjId, _slave.TlId, _owner.Id, scroll.SlaveDbId));
        await Assert.That(states.Single(state => state.ObjectId == cannon.ObjId))
            .IsEqualTo((cannon.ObjId, cannon.TlId, 0u, cannon.Id));
        // The stock visibility handler selects the TL only for this character ID and a saved vehicle.
        var active = states.Where(state => state.CharacterId == observer.Id && state.VehicleId != 0)
            .Select(state => state.TlId).SingleOrDefault();
        await Assert.That(active).IsEqualTo(isOwner ? _slave.TlId : (ushort)0);
        await Assert.That(_session.Packets.Count(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCUnitStatePacket))
            .IsEqualTo(2);
    }

    [Test]
    public async Task EnterRegionWithoutVehicle_SendsNoVehicleState()
    {
        _world.Regions[1, 0].AddToCharacters(_owner);
        await Assert.That(_session.Packets).IsEmpty();
    }

    private static (uint ObjectId, ushort TlId, uint CharacterId, uint VehicleId) ReadVisibleVehicle(byte[] packet)
    {
        var body = new PacketStream(packet, 8, packet.Length - 8);
        var objectId = body.ReadBc();
        var tlId = body.ReadUInt16();
        body.ReadUInt64();
        body.ReadInt32();
        body.ReadInt32();
        body.ReadString();
        return (objectId, tlId, body.ReadUInt32(), body.ReadUInt32());
    }
}
