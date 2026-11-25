using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public partial class MountSkillAuthorizationTests
{
    [Test]
    public async Task OwnHorse_RightClickOnRiddenBody_DismountsOnlyDriverAndPreservesHorse()
    {
        var connection = SetUpHorseInteraction();
        SetUpActorInventory();
        var item = new SummonMate(700, new SummonMateTemplate { Id = 8159, NpcId = 5431 }, 1)
        {
            OwnerId = _actor.Id, Slot = 0, SlotType = SlotType.Inventory
        };
        item._holdingContainer = _actor.Inventory.Bag;
        _actor.Inventory.Bag.Items.Add(item);
        _mate.ItemId = item.Id;
        var passenger = new ProbeCharacter { Id = 2, ObjId = 11, Hp = 100 };
        SetWorld(passenger, _world);
        _world.AddObject(passenger);
        _world.MateManager.MountMate(connection, _mate.TlId, AttachPointKind.Driver, AttachUnitReason.MountMateLeft);
        _world.MateManager.MountMate(new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = passenger },
            _mate.TlId, AttachPointKind.Passenger0, AttachUnitReason.MountMateLeft);
        await Assert.That(_actor.IsRiding && passenger.IsRiding).IsTrue();

        InteractWithMate(connection, _mate.ObjId);

        await Assert.That(_actor.Transform.Parent).IsNull();
        await Assert.That(_actor.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(_actor.IsRiding).IsFalse();
        await Assert.That(_mate.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(0u);
        await Assert.That(_mate.Passengers[AttachPointKind.Passenger0]._objId).IsEqualTo(passenger.ObjId);
        await Assert.That(passenger.Transform.Parent).IsSameReferenceAs(_mate.Transform);
        await Assert.That(_world.GetUnit(_mate.ObjId)).IsSameReferenceAs(_mate);
        await Assert.That(_mate.Hp).IsEqualTo(100);
        await Assert.That(_mate.ItemId).IsEqualTo(item.Id);
        await Assert.That(_actor.Inventory.Bag.GetItemBySlot(0)).IsSameReferenceAs(item);
        await Assert.That(item.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task OwnHorse_DiscoveryPreservesUnmountedHorseAndPassenger(bool seated, bool otherOwner)
    {
        var connection = SetUpHorseInteraction();
        var clicked = otherOwner ? MountOtherDriver() : _mate;
        if (seated)
            _world.MateManager.MountMate(connection, clicked.TlId, AttachPointKind.Passenger0, AttachUnitReason.MountMateLeft);

        var session = Mock.Of<ISession>();
        connection = new GameConnection(session.Object) { ActiveChar = _actor };
        _actor.Connection = connection;
        InteractWithMate(connection, clicked.ObjId);

        session.SendPacket(Is<byte[]>(IsMountingAction)).WasCalled(Times.Once);
        await Assert.That(_actor.IsRiding).IsEqualTo(seated);
        await Assert.That(clicked.Passengers[AttachPointKind.Passenger0]._objId).IsEqualTo(seated ? _actor.ObjId : 0u);
    }

    [Test]
    public async Task OwnHorse_SelectionAndClicksOnOtherObjectsPreserveDriver_PetBarStillDismounts()
    {
        var connection = SetUpHorseInteraction();
        _world.MateManager.MountMate(connection, _mate.TlId, AttachPointKind.Driver, AttachUnitReason.MountMateLeft);
        new CSChangeTargetPacket { Connection = connection }.Read(new PacketStream().WriteBc(_mate.ObjId));
        await Assert.That(_actor.CurrentTarget).IsSameReferenceAs(_mate);
        await Assert.That(_actor.IsRiding).IsTrue();

        var other = MountOtherDriver();
        InteractWithMate(connection, other.ObjId);
        InteractWithMate(connection, _mate.ObjId, mouseButton: 1);
        InteractWithMate(connection, _mate.ObjId, modifierKeys: 1);
        await Assert.That(_actor.Transform.Parent).IsSameReferenceAs(_mate.Transform);
        await Assert.That(_mate.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(_actor.ObjId);
        await Assert.That(other.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(other.OwnerObjId);

        new CSUnMountMatePacket { Connection = connection }.Read(new PacketStream().Write(_mate.TlId)
            .Write((byte)AttachPointKind.Driver).Write((byte)AttachUnitReason.None));
        await Assert.That(_actor.IsRiding).IsFalse();
        await Assert.That(_actor.Transform.Parent).IsNull();
    }

    private Mate MountOtherDriver()
    {
        var owner = new ProbeCharacter { Id = 2, ObjId = 12, Hp = 100 };
        var mate = new Mate
        {
            ObjId = 21, TlId = 31, OwnerObjId = owner.ObjId, OwnerId = owner.Id,
            Hp = 100, Template = new NpcTemplate { Scale = 1 }
        };
        SetWorld(owner, _world);
        SetWorld(mate, _world);
        _world.AddObject(owner);
        _world.AddObject(mate);
        _world.MateManager.TrackActiveMate(owner.Id, mate);
        _world.MateManager.MountMate(new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = owner },
            mate.TlId, AttachPointKind.Driver, AttachUnitReason.MountMateLeft);
        return mate;
    }

    private GameConnection SetUpHorseInteraction()
    {
        _world.AddObject(_actor);
        _world.AddObject(_mate);
        _mate.OwnerId = _actor.Id;
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _actor };
        _actor.Connection = connection;
        return connection;
    }

    private static void InteractWithMate(GameConnection connection, uint clickedId, byte mouseButton = 3, int modifierKeys = 0) =>
        new CSStartInteractionPacket { Connection = connection }.Read(new PacketStream()
            .WriteBc(clickedId).WriteBc(0).Write(1).Write(0).Write(mouseButton).Write(modifierKeys));

    private static bool IsMountingAction(byte[] bytes)
    {
        if (BitConverter.ToUInt16(bytes, 6) != SCOffsets.SCNpcInteractionSkillListPacket)
            return false;
        var packet = new PacketStream(bytes, 8, bytes.Length - 8);
        packet.ReadBc();
        packet.ReadBc();
        packet.ReadInt32();
        packet.ReadInt32();
        packet.ReadByte();
        return packet.ReadInt32() == 1 && packet.ReadUInt32() == SkillsEnum.SlaveMounting;
    }
}
