using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class SlaveRemovalTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private WorldInstance _world;
    private WorldInstance _otherWorld;
    private RecordingSession _session;
    private RecordingCharacter _owner;
    private Slave _slave;
    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        var worldManager = new WorldManager(null, null, null, null, null);
        Install(worldManager);
        Install(new PermissionManager(Mock.Of<IAccountManager>().Object));
        Install(new SlaveGameData());
        _world = new WorldInstance(new WorldTemplate
        { CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 0)
        { Regions = new Region[16, 16] };
        for (var x = 0; x < 16; x++)
        for (var y = 0; y < 16; y++)
            _world.Regions[x, y] = new Region(_world, x, y, 0);
        _otherWorld = new WorldInstance(new WorldTemplate(), 0, true, 1);
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>
        { [0] = _world, [1] = _otherWorld });
        _world.SlaveManager = new SlaveManager(_world);
        _world.SpawnManager = new SpawnManager(_world);
        typeof(WorldInstance).GetProperty(nameof(WorldInstance.Physics))!
            .SetValue(_world, new PhysicsManager { SimulationWorld = _world });
        _session = new RecordingSession();
        var connection = new GameConnection(_session);
        _owner = new RecordingCharacter
        { Id = 1, ObjId = 10, Hp = 100, Connection = connection, ParentWorld = _world };
        _owner.Region = _world.Regions[0, 0];
        connection.ActiveChar = _owner;
        _slave = NewSlave(20, 30);
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlayerPackets_RejectAnotherOwnersVehicle(bool destroyPacket)
    {
        // Object IDs alone do not prove ownership after an old session retires.
        _slave.Summoner = new RecordingCharacter { Id = _owner.Id, ObjId = _owner.ObjId, Hp = 100 };

        RequestRemoval(destroyPacket);

        await AssertUnchanged();
        await Assert.That(_session.Packets).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlayerPackets_RejectVehicleCombat(bool destroyPacket)
    {
        if (destroyPacket)
            ExpireVisibility();
        _slave.IsInBattle = true;

        RequestRemoval(destroyPacket);

        await AssertUnchanged();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveCannotRemoveWhileInCombat);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlayerRemoval_RejectsDifferentWorld(bool moveOwner)
    {
        (moveOwner ? (GameObject)_owner : _slave).ParentWorld = _otherWorld;

        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsFalse();

        await AssertUnchanged();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveDespawnNearTheSlave);
    }

    [Test]
    [Arguments(2, 0, true)]
    [Arguments(2, 2, true)]
    [Arguments(3, 0, false)]
    [Arguments(0, 3, false)]
    public async Task PlayerRemoval_UsesActorVisibilityNeighborhoodBoundary(int x, int y, bool accepted)
    {
        _slave.Region.RemoveObject(_slave);
        _slave.Region = _world.Regions[x, y];
        _slave.Transform.Local.Position = new Vector3(x * WorldManager.REGION_SIZE, y * WorldManager.REGION_SIZE, 1000);
        _slave.Region.AddObject(_slave);

        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsEqualTo(accepted);
        await Assert.That(Despawns().Contains(_slave)).IsEqualTo(accepted);
        if (!accepted)
        {
            await AssertUnchanged();
            await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveDespawnNearTheSlave);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlayerPackets_RejectHiddenOrRemovedActors(bool destroyPacket)
    {
        _slave.IsVisible = false;
        RequestRemoval(destroyPacket);
        await AssertUnchanged();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveDespawnNearTheSlave);

        _slave.IsVisible = true;
        _slave.Region.RemoveObject(_slave);
        RequestRemoval(destroyPacket);
        await AssertUnchanged();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveDespawnNearTheSlave);

        _slave.Region.AddObject(_slave);
        RequestRemoval(false);
        await Assert.That(_world.GetAllSlaves()).IsEmpty();
    }

    [Test]
    [Arguments(299, false)]
    [Arguments(301, true)]
    public async Task AutomaticRemoval_RequiresServerObservedContinuousAbsence(int seconds, bool accepted)
    {
        _slave.Region.RemoveObject(_slave);
        _slave.RecordOwnerVisibility(_owner, false, DateTime.UtcNow.AddSeconds(-seconds));

        RequestRemoval(true);

        await Assert.That(Despawns().Contains(_slave)).IsEqualTo(accepted);
        if (!accepted)
        {
            await AssertUnchanged();
            await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveDespawnNearTheSlave);
        }
    }

    [Test]
    public async Task AutomaticRemoval_RequiresLossEvidenceAndRejectsVisibleActors()
    {
        _slave.Region.RemoveObject(_slave);
        RequestRemoval(true);
        await AssertUnchanged();

        _slave.RecordOwnerVisibility(_owner, false, DateTime.UtcNow.AddSeconds(-301));
        _slave.Region.AddObject(_slave);
        RequestRemoval(true);
        await AssertUnchanged();
    }

    [Test]
    public async Task OwnerVisibility_UsesExact300SecondBoundaryAndResetsOnReappearance()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        _slave.RecordOwnerVisibility(_owner, false, now);
        _slave.RecordOwnerVisibility(_owner, false, now.AddSeconds(20));
        await Assert.That(_slave.HasExpiredOwnerVisibility(now.AddSeconds(300).AddTicks(-1))).IsFalse();
        await Assert.That(_slave.HasExpiredOwnerVisibility(now.AddSeconds(300))).IsTrue();

        var other = new RecordingCharacter { Id = 2, ObjId = 11 };
        _slave.RecordOwnerVisibility(other, true, now.AddSeconds(301));
        await Assert.That(_slave.HasExpiredOwnerVisibility(now.AddSeconds(301))).IsTrue();
        _slave.RecordOwnerVisibility(_owner, true, now.AddSeconds(302));
        await Assert.That(_slave.HasExpiredOwnerVisibility(now.AddSeconds(1000))).IsFalse();
        _slave.RecordOwnerVisibility(_owner, false, now.AddSeconds(310));
        await Assert.That(_slave.HasExpiredOwnerVisibility(now.AddSeconds(609))).IsFalse();
        await Assert.That(_slave.HasExpiredOwnerVisibility(now.AddSeconds(610))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnerVisibility_TracksBothCharacterAndVehicleRegionDeparture(bool characterDeparture)
    {
        var before = DateTime.UtcNow;
        if (characterDeparture)
            _slave.Region.RemoveFromCharacters(_owner);
        else
            _slave.RemoveVisibleObject(_owner);
        var after = DateTime.UtcNow;

        await Assert.That(_slave.HasExpiredOwnerVisibility(before.AddSeconds(300).AddTicks(-1))).IsFalse();
        await Assert.That(_slave.HasExpiredOwnerVisibility(after.AddSeconds(300))).IsTrue();
    }

    [Test]
    public async Task AutomaticRemoval_DoesNotBypassCargoOrManualVisibilityRules()
    {
        ExpireVisibility();
        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsFalse();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveDespawnNearTheSlave);
        AddCargo(false);
        RequestRemoval(true);
        await AssertUnchanged();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveEquipmentLoadedItem);
    }

    [Test]
    public async Task PlayerRemoval_UsesVehicleCombatRatherThanOwnerCombat()
    {
        _owner.IsInBattle = true;

        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlayerRemoval_RejectsDeadOrRetiredVehicle(bool retired)
    {
        if (retired)
            _slave.AttachmentsRetired = true;
        else
            _slave.Hp = 0;

        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsFalse();
        await Assert.That(_world.SlaveManager.GetSlaveByObjId(_slave.ObjId)).IsSameReferenceAs(_slave);
        await Assert.That(Despawns()).IsEmpty();
    }

    [Test]
    public async Task PlayerRemoval_CannotRemoveAttachedCannonAsAnOwnedVehicle()
    {
        var child = NewSlave(21, 31, BaseUnitType.Slave);
        child.ParentObj = _slave;
        _slave.AttachedSlaves.Add(child);

        await Assert.That(_world.SlaveManager.Delete(_owner, child.ObjId)).IsFalse();
        await Assert.That(_world.SlaveManager.RemoveActiveSlave(_owner, child.TlId)).IsFalse();
        await Assert.That(_world.SlaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsSameReferenceAs(_slave);
        _world.RemoveObject(_slave);
        await Assert.That(_world.SlaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNull();
        await Assert.That(child.AttachmentsRetired).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CargoRejection_KeepsPassengersAndDoodads(bool templateOnly)
    {
        var cargo = AddCargo(templateOnly);
        Attach(_owner, _slave);

        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsFalse();

        await AssertUnchanged();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveEquipmentLoadedItem);
        await Assert.That(_owner.Transform.Parent).IsSameReferenceAs(_slave.Transform);
        await Assert.That(_slave.AttachedCharacters[AttachPointKind.Driver]).IsSameReferenceAs(_owner);
        await Assert.That(cargo.IsPersistent).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PlayerRemoval_SaveFailureKeepsVehiclePassengersAndAttachments(bool automatic, bool throws)
    {
        _slave.Id = 123;
        typeof(Slave).GetProperty(nameof(Slave.SummoningItem))!.SetValue(_slave, new SummonSlave());
        Attach(_owner, _slave);
        var doodad = new Doodad { ObjId = 22, IsPersistent = true, ParentWorld = _world };
        doodad.Transform.Parent = _slave.Transform;
        _slave.AttachedDoodads.Add(doodad);
        if (automatic)
            ExpireVisibility();
        var saves = 0;
        _world.SlaveManager.SaveForRemoval = vehicle =>
        {
            saves++;
            if (!ReferenceEquals(vehicle, _slave))
                throw new InvalidOperationException("The wrong vehicle reached persistence.");
            return throws ? throw new InvalidOperationException("Forced vehicle save failure") : false;
        };

        var accepted = automatic
            ? _world.SlaveManager.RemoveActiveSlave(_owner, _slave.TlId)
            : _world.SlaveManager.Delete(_owner, _slave.ObjId);

        await Assert.That(accepted).IsFalse();
        await Assert.That(saves).IsEqualTo(1);
        await AssertUnchanged();
        await Assert.That(LastError()).IsEqualTo(ErrorMessageType.InternalError);
        await Assert.That(_owner.Transform.Parent).IsSameReferenceAs(_slave.Transform);
        await Assert.That(_slave.AttachedCharacters[AttachPointKind.Driver]).IsSameReferenceAs(_owner);
        await Assert.That(doodad.IsPersistent).IsTrue();
        await Assert.That(doodad.Transform.Parent).IsSameReferenceAs(_slave.Transform);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlayerRemoval_NonpersistentVehicleDoesNotNeedSuccessfulSave(bool hasSummoningItem)
    {
        _slave.Id = hasSummoningItem ? 0u : 123u;
        if (hasSummoningItem)
            typeof(Slave).GetProperty(nameof(Slave.SummoningItem))!.SetValue(_slave, new SummonSlave());
        _world.SlaveManager.SaveForRemoval = _ => false;

        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsTrue();
        await Assert.That(_slave.AttachmentsRetired).IsTrue();
        await Assert.That(_world.GetAllSlaves()).IsEmpty();
    }

    [Test]
    public async Task InternalCleanup_SaveFailureKeepsExistingRemovalPolicy()
    {
        _slave.Id = 123;
        typeof(Slave).GetProperty(nameof(Slave.SummoningItem))!.SetValue(_slave, new SummonSlave());
        _world.SlaveManager.SaveForRemoval = _ => false;

        _world.SlaveManager.RemoveAndDespawnAllActiveOwnedSlaves(_owner);

        await Assert.That(_slave.AttachmentsRetired).IsTrue();
        await Assert.That(_world.GetAllSlaves()).IsEmpty();
        await Assert.That(_session.Packets).IsEmpty();
    }

    [Test]
    public async Task AcceptedRemoval_DetachesParentAndChildPassengersAndSchedulesOnce()
    {
        Attach(_owner, _slave);
        var child = NewSlave(21, 31);
        _slave.AttachedSlaves.Add(child);
        child.Transform.Parent = _slave.Transform;
        var passenger = new RecordingCharacter { ObjId = 11, Hp = 100, ParentWorld = _world };
        Attach(passenger, child);
        var doodad = new Doodad { ObjId = 22, IsPersistent = true, ParentWorld = _world };
        doodad.Transform.Parent = _slave.Transform;
        _slave.AttachedDoodads.Add(doodad);

        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsTrue();
        await Assert.That(_world.SlaveManager.Delete(_owner, _slave.ObjId)).IsFalse();

        await Assert.That(_world.GetAllSlaves()).IsEmpty();
        await Assert.That(_owner.Transform.Parent).IsNull();
        await Assert.That(passenger.Transform.Parent).IsNull();
        await Assert.That(_owner.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(passenger.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(_slave.AttachedCharacters).IsEmpty();
        await Assert.That(child.AttachedCharacters).IsEmpty();
        await Assert.That(_slave.AttachmentsRetired && child.AttachmentsRetired).IsTrue();
        await Assert.That(doodad.IsPersistent).IsFalse();
        await Assert.That(doodad.Transform.Parent).IsNull();
        await Assert.That(Despawns().SetEquals([_slave, child, doodad])).IsTrue();
        await Assert.That(_owner.Broadcasts.OfType<SCSlaveDespawnPacket>().Count()).IsEqualTo(1);
        await Assert.That(_owner.Broadcasts.OfType<SCSlaveRemovedPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InternalCleanup_BypassesPlayerWorldRangeAndCombat(bool scheduledCleanup)
    {
        _slave.IsInBattle = true;
        _owner.ParentWorld = _otherWorld;
        _owner.Transform.Local.Position = new Vector3(10000, 10000, 10000);

        if (scheduledCleanup)
        {
            _slave.Summoner = null; // Mirage test vehicles are not player-owned.
            _world.SlaveManager.RemoveAndDespawnTestSlave(_owner, _slave.ObjId);
        }
        else
            _world.SlaveManager.RemoveAndDespawnAllActiveOwnedSlaves(_owner);

        await Assert.That(_world.GetAllSlaves()).IsEmpty();
        await Assert.That(Despawns().Contains(_slave)).IsTrue();
        await Assert.That(_session.Packets).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InternalCleanup_PreservesExplicitCargoPolicy(bool force)
    {
        var cargo = AddCargo(false);
        _slave.IsInBattle = true;

        _world.SlaveManager.RemoveAndDespawnAllActiveOwnedSlaves(_owner, force);

        await Assert.That(Despawns().Contains(_slave)).IsEqualTo(force);
        await Assert.That(Despawns().Contains(cargo)).IsEqualTo(force);
        await Assert.That(cargo.IsPersistent).IsEqualTo(!force);
        if (!force)
            await Assert.That(LastError()).IsEqualTo(ErrorMessageType.SlaveEquipmentLoadedItem);
    }

    private Slave NewSlave(uint objId, ushort tlId, BaseUnitType ownerType = BaseUnitType.Character)
    {
        var slave = new Slave
        {
            ObjId = objId, TlId = tlId, Hp = 100, Summoner = _owner,
            ParentWorld = _world, Template = new SlaveTemplate { PortalTime = 3 },
            IsVisible = true, Region = _world.Regions[0, 0], OwnerType = ownerType
        };
        _world.AddObject(slave);
        slave.Region.AddObject(slave);
        return slave;
    }

    private Doodad AddCargo(bool templateOnly)
    {
        var cargo = new Doodad
        {
            ObjId = 22, IsPersistent = true, ParentWorld = _world,
            ItemId = templateOnly ? 0u : 77u, ItemTemplateId = templateOnly ? 88u : 0u
        };
        _slave.AttachedDoodads.Add(cargo);
        return cargo;
    }

    private void ExpireVisibility()
    {
        _slave.Region.RemoveObject(_slave);
        _slave.RecordOwnerVisibility(_owner, false, DateTime.UtcNow.AddSeconds(-301));
    }

    private static void Attach(Character character, Slave slave)
    {
        character.Transform.Parent = slave.Transform;
        character.AttachedPoint = AttachPointKind.Driver;
        character.Buffs = Mock.Of<IBuffs>().Object;
        slave.AttachedCharacters[AttachPointKind.Driver] = character;
    }

    private void RequestRemoval(bool destroyPacket)
    {
        if (destroyPacket)
            new CSDestroySlavePacket { Connection = _owner.Connection }
                .Read(new PacketStream().Write(_slave.TlId));
        else
            new CSDespawnSlavePacket { Connection = _owner.Connection }
                .Read(new PacketStream().WriteBc(_slave.ObjId));
    }

    private async Task AssertUnchanged()
    {
        await Assert.That(_world.SlaveManager.GetSlaveByObjId(_slave.ObjId)).IsSameReferenceAs(_slave);
        await Assert.That(_slave.AttachmentsRetired).IsFalse();
        await Assert.That(Despawns()).IsEmpty();
        await Assert.That(_owner.Broadcasts).IsEmpty();
    }

    private HashSet<GameObject> Despawns() => (HashSet<GameObject>)typeof(SpawnManager)
        .GetProperty("Despawns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_world.SpawnManager)!;

    private ErrorMessageType LastError() =>
        (ErrorMessageType)BitConverter.ToInt16(_session.Packets.Last(packet =>
            BitConverter.ToUInt16(packet, 6) == SCOffsets.SCErrorMsgPacket), 8);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private void Install<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private sealed class RecordingCharacter() : Character(null)
    {
        internal List<GamePacket> Broadcasts { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Broadcasts.Add(packet);
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }

    private sealed class RecordingSession : ISession
    {
        internal List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
