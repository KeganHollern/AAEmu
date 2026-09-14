using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.Mate;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public class MountSkillAuthorizationTests
{
    private readonly Dictionary<FieldInfo, object> _instances = [];
    private Character _actor;
    private Mate _mate;
    private WorldInstance _world;
    private MateGameData _data;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new WorldManager(null, null, null, null, null));
        _world = new WorldInstance(new WorldTemplate(), 0, true, 0);
        _world.MateManager = new MateManager(_world);
        _world.SlaveManager = new SlaveManager(_world);
        _actor = new ProbeCharacter() { Id = 1, ObjId = 10, Hp = 100 };
        _mate = new Mate { ObjId = 20, OwnerObjId = 10, TlId = 30, Hp = 100, Level = 35,
            Template = new NpcTemplate { Scale = 1 }, Skills = [90, 83] };
        SetWorld(_actor, _world);
        SetWorld(_mate, _world);
        _world.MateManager.TrackActiveMate(_actor.Id, _mate);
        _data = new MateGameData();
        Set(_data, "_mountSkills", new Dictionary<uint, MountSkills>
        {
            [90] = new() { Id = 90, SkillId = 17693 },
            [83] = new() { Id = 83, SkillId = 17092 },
            [30] = new() { Id = 30, SkillId = 13567 },
            [999] = new() { Id = 999, SkillId = 17092 }
        });
        Set(_data, "_mountAttachedSkills", new Dictionary<uint, MountAttachedSkills>
        {
            [1] = new() { MountSkillId = 83, AttachPointId = AttachPointKind.Driver, SkillId = 18228 },
            [2] = new() { MountSkillId = 30, AttachPointId = AttachPointKind.Passenger0, SkillId = 17718 }
        });
        Set(_data, "_buffMountSkills", new Dictionary<uint, HashSet<uint>> { [1863] = [30] });
        SetInstance(_data);
        var seats = new MateSeatGameData();
        Set(seats, "_seats", new Dictionary<uint, HashSet<AttachPointKind>>
        { [_mate.ModelId] = [AttachPointKind.Driver, AttachPointKind.Passenger0] });
        SetInstance(seats);
        var tags = new TagsGameData();
        Set(tags, "_tags", new Dictionary<TagsGameData.TagType, Dictionary<uint, HashSet<uint>>>
        { [TagsGameData.TagType.Items] = new() { [29] = [200], [1259] = [201] } });
        SetInstance(tags);
        var skills = new SkillManager(null, null);
        Set(skills, "_skills", new Dictionary<uint, SkillTemplate>
        {
            [17693] = new() { Id = 17693, AbilityLevel = 35 },
            [17092] = new() { Id = 17092, AbilityLevel = 5 },
            [13567] = new() { Id = 13567, AbilityLevel = 1 }
        });
        SetInstance(skills);
        var slaves = new SlaveGameData();
        var slaveSkills = (Dictionary<uint, SlaveMountSkills>)typeof(SlaveGameData)
            .GetField("_slaveMountSkills", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slaves)!;
        slaveSkills.Add(1, new SlaveMountSkills { SlaveId = 40, MountSkillId = 83 });
        SetInstance(slaves);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, instance) in _instances)
            field.SetValue(null, instance);
    }

    [Test]
    public async Task OwnerBattlePetCommand_WorksWithoutMountingButChecksLevel()
    {
        await Assert.That(Authorize(_mate, 90, 17693, out var rider)).IsTrue();
        await Assert.That(rider).IsEqualTo(0u);
        _mate.Level = 34;
        await Assert.That(Authorize(_mate, 90, 17693, out _)).IsFalse();
    }

    [Test]
    public async Task RiderSkill_RequiresBothSeatRecordsAndExactAuthoredSkillPair()
    {
        await Assert.That(Authorize(_mate, 83, 17092, out _)).IsFalse();
        Attach(_mate, AttachPointKind.Driver);
        await Assert.That(Authorize(_mate, 83, 17092, out var rider)).IsTrue();
        await Assert.That(rider).IsEqualTo(18228u);
        await Assert.That(Authorize(_mate, 83, 17693, out _)).IsFalse();
        await Assert.That(Authorize(_mate, 999, 17092, out _)).IsFalse();
        _mate.Passengers[AttachPointKind.Driver]._objId = 0;
        await Assert.That(Authorize(_mate, 83, 17092, out _)).IsFalse();
    }

    [Test]
    public async Task OtherOwnerAndReusedMateIdentity_CannotIssueCommands()
    {
        _mate.OwnerObjId = 999;
        await Assert.That(Authorize(_mate, 90, 17693, out _)).IsFalse();
        _mate.OwnerObjId = _actor.ObjId;
        _world.MateManager.TryBeginMateRemoval(_actor.Id, _mate);
        _world.MateManager.CompleteMateRemoval(_mate);
        var replacement = new Mate { TlId = _mate.TlId, ObjId = _mate.ObjId, OwnerObjId = _actor.ObjId };
        _world.MateManager.TrackActiveMate(_actor.Id, replacement);
        await Assert.That(Authorize(_mate, 90, 17693, out _)).IsFalse();
    }

    [Test]
    public async Task DeathOrAnotherInstance_RejectsMountSkill()
    {
        _actor.Hp = 0;
        await Assert.That(Authorize(_mate, 90, 17693, out _)).IsFalse();
        _actor.Hp = 100;
        _mate.Hp = 0;
        await Assert.That(Authorize(_mate, 90, 17693, out _)).IsFalse();
        _mate.Hp = 100;
        SetWorld(_mate, new WorldInstance(new WorldTemplate(), 0, true, 1));
        await Assert.That(Authorize(_mate, 90, 17693, out _)).IsFalse();
    }

    [Test]
    public async Task VehicleBuff_ReplacesBaseSkillsAndChecksPassengerSeat()
    {
        var slave = new Slave { ObjId = 50, TemplateId = 40, Hp = 100, Level = 50,
            Template = new SlaveTemplate { Mountable = true } };
        SetWorld(slave, _world);
        _world.AddObject(slave);
        Attach(slave, AttachPointKind.Driver);
        await Assert.That(Authorize(slave, 83, 17092, out _)).IsTrue();
        var buff = AddStanceBuff(slave);
        slave.ActiveMountSkillBuffId = 1863;
        await Assert.That(Authorize(slave, 83, 17092, out _)).IsFalse();
        await Assert.That(Authorize(slave, 30, 13567, out _)).IsFalse();
        slave.AttachedCharacters.Clear();
        Attach(slave, AttachPointKind.Passenger0);
        await Assert.That(Authorize(slave, 30, 13567, out var rider)).IsTrue();
        await Assert.That(rider).IsEqualTo(17718u);
        buff.InUse = false;
        await Assert.That(Authorize(slave, 30, 13567, out _)).IsFalse();
    }

    [Test]
    public async Task MatePassenger_CanLeaveButCannotEjectDriverOrUseOwnerCommands()
    {
        _mate.OwnerObjId = 99;
        Attach(_mate, AttachPointKind.Passenger0);
        await Assert.That(_world.MateManager.CanRequestUnmount(_actor, _mate, AttachPointKind.Passenger0)).IsTrue();
        _mate.Passengers[AttachPointKind.Driver]._objId = 99;
        await Assert.That(_world.MateManager.CanRequestUnmount(_actor, _mate, AttachPointKind.Driver)).IsFalse();
        await Assert.That(Authorize(_mate, 90, 17693, out _)).IsFalse();
    }

    [Test]
    public async Task Owner_CanRemovePassengerButStrangerCannot()
    {
        _mate.Passengers[AttachPointKind.Passenger0]._objId = 99;
        await Assert.That(_world.MateManager.CanRequestUnmount(_actor, _mate, AttachPointKind.Passenger0)).IsTrue();
        _mate.OwnerObjId = 88;
        await Assert.That(_world.MateManager.CanRequestUnmount(_actor, _mate, AttachPointKind.Passenger0)).IsFalse();
    }

    [Test]
    [Arguments(2.99f, true)]
    [Arguments(3f, false)]
    [Arguments(30f, false)]
    [Arguments(float.NaN, false)]
    [Arguments(float.PositiveInfinity, false)]
    public async Task MateEntry_UsesNativeDistanceBoundary(float distance, bool expected)
    {
        _actor.Transform.Local.SetPosition(distance, 0, 0);
        await Assert.That(MountSeatAuthorization.CanEnter(_actor, _mate, Vector3.Zero, 3f)).IsEqualTo(expected);
    }

    [Test]
    public async Task Entry_DoesNotTransferAnAlreadyAttachedOrDeadCharacter()
    {
        Attach(_mate, AttachPointKind.Driver);
        await Assert.That(MountSeatAuthorization.CanEnter(_actor, _mate, Vector3.Zero, 3f)).IsFalse();
        _actor.Transform.Parent = null;
        _actor.IsRiding = false;
        _actor.AttachedPoint = AttachPointKind.None;
        _actor.Hp = 0;
        await Assert.That(MountSeatAuthorization.CanEnter(_actor, _mate, Vector3.Zero, 3f)).IsFalse();
    }

    [Test]
    public async Task EquipmentSnapshot_RejectsStaleIdentityAndWrongTemplate()
    {
        var item = new Item { Id = 10, TemplateId = 20 };
        await Assert.That(CSChangeMateEquipmentPacket.MatchesSnapshot(item, new Item { Id = 10, TemplateId = 20 })).IsTrue();
        await Assert.That(CSChangeMateEquipmentPacket.MatchesSnapshot(item, new Item { Id = 11, TemplateId = 20 })).IsFalse();
        await Assert.That(CSChangeMateEquipmentPacket.MatchesSnapshot(item, new Item { Id = 10, TemplateId = 21 })).IsFalse();
        await Assert.That(CSChangeMateEquipmentPacket.MatchesSnapshot(null, new Item { TemplateId = 20 })).IsFalse();
    }

    [Test]
    public async Task StateCommand_UsesAuthenticatedOwnerAndRejectsForeignTlId()
    {
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _actor };
        _world.MateManager.ChangeStateMate(connection, _mate.TlId, 2);
        await Assert.That(_mate.UserState).IsEqualTo((byte)2);
        _mate.OwnerObjId = 99;
        _world.MateManager.ChangeStateMate(connection, _mate.TlId, 1);
        await Assert.That(_mate.UserState).IsEqualTo((byte)2);
    }

    [Test]
    public async Task BuffReplacement_UsesLatestStartAndAnyGrantEndClearsIt()
    {
        Set(_data, "_buffMountSkills", new Dictionary<uint, HashSet<uint>> { [1863] = [30], [1526] = [83] });
        var first = new BuffTemplate { Id = 1863 };
        var second = new BuffTemplate { Id = 1526 };
        var firstBuff = new Buff(_mate, _mate, new SkillCasterUnit(_mate.ObjId), first, null, DateTime.UtcNow) { Passive = true };
        var secondBuff = new Buff(_mate, _mate, new SkillCasterUnit(_mate.ObjId), second, null, DateTime.UtcNow) { Passive = true };
        first.Start(_mate, _mate, firstBuff);
        await Assert.That(_mate.ActiveMountSkillBuffId).IsEqualTo(1863u);
        second.Start(_mate, _mate, secondBuff);
        await Assert.That(_mate.ActiveMountSkillBuffId).IsEqualTo(1526u);
        first.Dispel(_mate, _mate, firstBuff);
        await Assert.That(_mate.ActiveMountSkillBuffId).IsEqualTo(0u);
    }

    [Test]
    public async Task PetEquipment_UsesAuthoredSlotsArmorKindAndMateLevel()
    {
        Set(_data, "_equipmentSlots", new Dictionary<int, HashSet<EquipmentItemSlot>>
        { [1] = [EquipmentItemSlot.Head, EquipmentItemSlot.Waist, EquipmentItemSlot.Feet] });
        _mate.Template.MateEquipSlotPackId = 1;
        var container = new MateEquipmentContainer(_actor.Id, SlotType.EquipmentMate, false, _mate);
        var template = new ArmorTemplate { LevelRequirement = 35,
            WearableTemplate = new Wearable { TypeId = (uint)ArmorType.PetArmor, SlotTypeId = (uint)EquipmentItemSlotType.Head } };
        var item = new Armor { TemplateId = 200, Template = template };
        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Head)).IsTrue();
        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Chest)).IsFalse();
        item.TemplateId = 201;
        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Head)).IsFalse();
        item.TemplateId = 200;
        Set(_data, "_underwaterModels", new HashSet<uint> { _mate.ModelId });
        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Head)).IsFalse();
        Set(_data, "_underwaterModels", new HashSet<uint>());
        _mate.Level = 34;
        await Assert.That(container.CanAccept(item, (int)EquipmentItemSlot.Head)).IsFalse();
        await Assert.That(container.CanAccept(new Item(), (int)EquipmentItemSlot.Head)).IsFalse();
        // An old invalid item must still be removable from a valid container slot.
        await Assert.That(container.CanAccept(null, (int)EquipmentItemSlot.Chest)).IsTrue();
        await Assert.That(container.CanAccept(null, container.ContainerSize)).IsFalse();
    }

    [Test]
    public async Task VehicleEntry_UsesRotatedSeatPositionAndRejectsUnauthoredSeats()
    {
        var slave = new Slave { ObjId = 50, ModelId = 128, TemplateId = 40, Hp = 100,
            Template = new SlaveTemplate { Mountable = true } };
        SetWorld(slave, _world);
        slave.Transform.Local.SetPosition(100, 100, 0);
        slave.Transform.Local.SetRotation(0, 0, MathF.PI / 2);
        Set(SlaveGameData.Instance, "_attachPoints", new Dictionary<uint, Dictionary<AttachPointKind, WorldSpawnPosition>>
        {
            [128] = new() { [AttachPointKind.Driver] = new WorldSpawnPosition { X = 10, Y = 0, Z = 0 } }
        });
        await Assert.That(SlaveManager.TryGetSeatPosition(slave, AttachPointKind.Driver, null, out var position)).IsTrue();
        await Assert.That(Vector3.Distance(position, new Vector3(100, 110, 0)) < 0.001f).IsTrue();
        _actor.Transform.Local.SetPosition(100, 110, 0);
        await Assert.That(MountSeatAuthorization.CanEnter(_actor, slave, position, 4f)).IsTrue();
        _actor.Transform.Local.SetPosition(100, 100, 0);
        await Assert.That(MountSeatAuthorization.CanEnter(_actor, slave, position, 4f)).IsFalse();
        await Assert.That(SlaveManager.TryGetSeatPosition(slave, AttachPointKind.Hook, null, out _)).IsFalse();
        slave.Template.Mountable = false;
        await Assert.That(SlaveManager.TryGetSeatPosition(slave, AttachPointKind.Driver, null, out _)).IsFalse();
    }

    [Test]
    public async Task EntryPacketPath_RejectedDistanceKeepsSeatAndBuffsUnchanged()
    {
        var buffs = Mock.Of<IBuffs>();
        _actor.Buffs = buffs.Object;
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _actor };
        _actor.Transform.Local.SetPosition(100, 0, 0);
        _world.MateManager.MountMate(connection, _mate.TlId, AttachPointKind.Driver, AttachUnitReason.MountMateLeft);
        await Assert.That(_mate.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(0u);
        await Assert.That(_actor.Transform.Parent).IsNull();
        buffs.TriggerRemoveOn(BuffRemoveOn.Mount, 0).WasCalled(Times.Never);
        _actor.Transform.Local.SetPosition(1, 0, 0);
        _world.MateManager.MountMate(connection, _mate.TlId, AttachPointKind.Driver, AttachUnitReason.MountMateLeft);
        await Assert.That(_mate.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(_actor.ObjId);
        await Assert.That(_actor.Transform.Parent).IsSameReferenceAs(_mate.Transform);
        buffs.TriggerRemoveOn(BuffRemoveOn.Mount, 0).WasCalled(Times.Once);
    }

    [Test]
    public async Task ConcurrentPassengerEntry_ClaimsSeatOnce()
    {
        _mate.OwnerObjId = 99;
        var other = new ProbeCharacter { Id = 2, ObjId = 11, Hp = 100 };
        SetWorld(other, _world);
        var first = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _actor };
        var second = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = other };
        await Task.WhenAll(Task.Run(() => _world.MateManager.MountMate(first, _mate.TlId,
                AttachPointKind.Passenger0, AttachUnitReason.MountMateLeft)),
            Task.Run(() => _world.MateManager.MountMate(second, _mate.TlId,
                AttachPointKind.Passenger0, AttachUnitReason.MountMateLeft)));
        var occupant = _mate.Passengers[AttachPointKind.Passenger0]._objId;
        await Assert.That(occupant == _actor.ObjId || occupant == other.ObjId).IsTrue();
        await Assert.That(new[] { _actor, other }.Count(actor => actor.IsRiding)).IsEqualTo(1);
    }

    [Test]
    public async Task OwnerEjection_StaleSeatCannotDetachPassengerFromAnotherMount()
    {
        var passenger = new ProbeCharacter { Id = 2, ObjId = 11, Hp = 100 };
        SetWorld(passenger, _world);
        _world.AddObject(passenger);
        var other = new Mate { ObjId = 21, Hp = 100, Template = new NpcTemplate { Scale = 1 } };
        SetWorld(other, _world);
        passenger.Transform.Parent = other.Transform;
        passenger.Transform.Local.SetPosition(0, 0, 0);
        passenger.AttachedPoint = AttachPointKind.Passenger0;
        passenger.IsRiding = true;
        _mate.Passengers[AttachPointKind.Passenger0]._objId = passenger.ObjId;

        _world.MateManager.UnMountMate(_actor, _mate.TlId, AttachPointKind.Passenger0, AttachUnitReason.TransferBinding);

        await Assert.That(passenger.Transform.Parent).IsSameReferenceAs(other.Transform);
        await Assert.That(passenger.IsRiding).IsTrue();
        await Assert.That(_mate.Passengers[AttachPointKind.Passenger0]._objId).IsEqualTo(0u);
    }

    private sealed class ProbeCharacter() : Character(null)
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }

    [Test]
    [Arguments(EffectState.Acting, true, 0, 0, true)]
    [Arguments(EffectState.Acting, true, 60000, 0, true)]
    [Arguments(EffectState.Acting, false, 0, 0, false)]
    [Arguments(EffectState.Finishing, true, 0, 0, false)]
    [Arguments(EffectState.Finished, true, 0, 0, false)]
    [Arguments(EffectState.Acting, true, 1000, 2000, false)]
    public async Task VehicleStance_OnlyActiveUnexpiredBuffGrantsSkills(EffectState state, bool inUse,
        int duration, int elapsed, bool expected)
    {
        var slave = new Slave { ObjId = 50, TemplateId = 40, Hp = 100, Level = 50,
            Template = new SlaveTemplate { Mountable = true } };
        SetWorld(slave, _world);
        _world.AddObject(slave);
        Attach(slave, AttachPointKind.Passenger0);
        var buff = AddStanceBuff(slave);
        buff.State = state;
        buff.InUse = inUse;
        buff.Duration = duration;
        buff.StartTime = DateTime.UtcNow.AddMilliseconds(-elapsed);
        slave.ActiveMountSkillBuffId = 1863;

        await Assert.That(Authorize(slave, 30, 13567, out _)).IsEqualTo(expected);
        await Assert.That(Authorize(slave, 83, 17092, out _)).IsFalse();
    }

    private static Buff AddStanceBuff(Slave slave)
    {
        var buff = new Buff(slave, slave, new SkillCasterUnit(slave.ObjId),
            new BuffTemplate { Id = 1863 }, null, DateTime.UtcNow)
        { State = EffectState.Acting, InUse = true };
        var effects = (List<Buff>)typeof(Buffs).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(slave.Buffs)!;
        effects.Add(buff);
        return buff;
    }

    [Test]
    [Arguments("slot")]
    [Arguments("container")]
    [Arguments("eligibility")]
    public async Task InvalidOwnedEquipment_ReturnsFailureWithoutMovingTheItem(string invalid)
    {
        var items = new ItemManager(null, null, null, null, null, null);
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>().Where(type => type != SlotType.EquipmentMate))
        {
            var container = new ItemContainer(_actor.Id, type, false, _actor)
            { ContainerId = (ulong)containers.Count + 1, Owner = _actor };
            containers.Add(container.ContainerId, container);
        }
        Set(items, "_allPersistentContainers", containers);
        SetInstance(items);
        _actor.NumInventorySlots = 10;
        _actor.Inventory = new Inventory(_actor);
        Set(_data, "_equipmentSlots", new Dictionary<int, HashSet<EquipmentItemSlot>>
        { [1] = [EquipmentItemSlot.Head] });
        _mate.Template.MateEquipSlotPackId = 1;
        var item = new Armor { Id = 700, TemplateId = 200, Count = 1, OwnerId = _actor.Id,
            Slot = 0, SlotType = SlotType.Inventory,
            Template = new ArmorTemplate { WearableTemplate = new Wearable
            { SlotTypeId = (uint)(invalid == "eligibility" ? EquipmentItemSlotType.Chest : EquipmentItemSlotType.Head) } } };
        item._holdingContainer = _actor.Inventory.Bag;
        _actor.Inventory.Bag.Items.Add(item);
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = _actor };
        _actor.Connection = connection;
        var petSlot = invalid == "slot" ? (byte)255 : (byte)EquipmentItemSlot.Head;
        var body = new PacketStream().Write(999u).Write(_mate.TlId).Write(888u).Write(false).Write((byte)1)
            .Write(item).Write(0u).Write((byte)(invalid == "container" ? SlotType.Bank : SlotType.Inventory))
            .Write((byte)0).Write((byte)SlotType.EquipmentMate).Write(petSlot);

        new CSChangeMateEquipmentPacket { Connection = connection }.Read(body);

        await Assert.That(_actor.Inventory.Bag.GetItemBySlot(0)).IsSameReferenceAs(item);
        await Assert.That(_mate.Equipment.Items).IsEmpty();
        var expected = new SCMateEquipmentChangedPacket(
            new ItemAndLocation { Item = item, SlotType = SlotType.Inventory, SlotNumber = 0 },
            new ItemAndLocation { SlotType = SlotType.EquipmentMate, SlotNumber = petSlot },
            _mate.TlId, _actor.Id, 0, false, false).Encode().GetBytes();
        await Assert.That(expected[^1]).IsEqualTo((byte)0);
        session.SendPacket(Is<byte[]>(packet => packet.SequenceEqual(expected))).WasCalled(Times.Once);
    }

    private bool Authorize(BaseUnit caster, uint mountSkill, uint skill, out uint rider) =>
        MountSkillAuthorization.TryAuthorize(_actor, caster,
            new SkillCasterMount(caster.ObjId) { MountSkillTemplateId = mountSkill }, skill, out rider);

    private void Attach(Unit unit, AttachPointKind seat)
    {
        _actor.Transform.Parent = unit.Transform;
        _actor.Transform.Local.SetPosition(0, 0, 0);
        _actor.AttachedPoint = seat;
        _actor.IsRiding = unit is Mate;
        if (unit is Mate mate)
            mate.Passengers[seat]._objId = _actor.ObjId;
        else if (unit is Slave slave)
            slave.AttachedCharacters[seat] = _actor;
    }

    private static void SetWorld(GameObject unit, WorldInstance world) =>
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unit, world);

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _instances.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }
}
