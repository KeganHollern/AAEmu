using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class MountRetirementTests
{
    private static readonly FieldInfo SlaveDataInstance = typeof(Singleton<SlaveGameData>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MateSeatDataInstance = typeof(Singleton<MateSeatGameData>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PermissionInstance = typeof(Singleton<PermissionManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previousSlaveData;
    private object _previousMateSeats;
    private object _previousPermissions;
    private WorldInstance _world;
    private ProbeCharacter _character;

    [Before(Test)]
    public void SetUp()
    {
        _previousPermissions = PermissionInstance.GetValue(null);
        PermissionInstance.SetValue(null, new PermissionManager(Mock.Of<IAccountManager>().Object));
        _previousSlaveData = SlaveDataInstance.GetValue(null);
        SlaveDataInstance.SetValue(null, new SlaveGameData());
        _previousMateSeats = MateSeatDataInstance.GetValue(null);
        var mateSeats = new MateSeatGameData();
        typeof(MateSeatGameData).GetField("_seats", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mateSeats, new Dictionary<uint, HashSet<AttachPointKind>>
            { [0] = [AttachPointKind.Driver, AttachPointKind.Passenger0] });
        MateSeatDataInstance.SetValue(null, mateSeats);
        _world = new WorldInstance(new WorldTemplate(), 0, true, 0);
        _world.MateManager = new MateManager(_world);
        _world.SlaveManager = new SlaveManager(_world);
        _character = new ProbeCharacter { Id = 1, ObjId = 10, Hp = 100 };
        SetWorld(_character);
    }

    [After(Test)]
    public void TearDown()
    {
        SlaveDataInstance.SetValue(null, _previousSlaveData);
        MateSeatDataInstance.SetValue(null, _previousMateSeats);
        PermissionInstance.SetValue(null, _previousPermissions);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MateEntry_AfterLookupRejectsRemovedOrRetiringMate(bool keepTrackedDuringCleanup)
    {
        var mate = new Mate { ObjId = 20, TlId = 30, OwnerObjId = 99, Hp = 100,
            Template = new NpcTemplate { Scale = 1 } };
        SetWorld(mate);
        _world.MateManager.TrackActiveMate(2, mate);
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _character };

        RunEntryAfterRetirement(
            () => _world.MateManager.MountMate(connection, mate.TlId, AttachPointKind.Passenger0,
                AttachUnitReason.MountMateLeft),
            () =>
            {
                if (keepTrackedDuringCleanup)
                    mate.TryRunDespawnLifecycle(_ => { });
                else
                    _world.MateManager.TryBeginMateRemoval(2, mate);
            });

        await Assert.That(_character.Transform.Parent).IsNull();
        await Assert.That(_character.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(_character.IsRiding).IsFalse();
        await Assert.That(mate.Passengers[AttachPointKind.Passenger0]._objId).IsEqualTo(0u);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SlaveEntry_AfterLookupRejectsRemovedOrRetiringSlave(bool keepVisibleDuringCleanup)
    {
        var slave = CreateSlave();

        RunEntryAfterRetirement(
            () => _world.SlaveManager.BindSlave(_character, slave.ObjId, AttachPointKind.Driver,
                AttachUnitReason.NewMaster),
            () =>
            {
                if (keepVisibleDuringCleanup)
                    _world.SlaveManager.TryBeginAttachmentRemoval(slave, out _);
                else
                    _world.RemoveObject(slave);
            });

        await Assert.That(_character.Transform.Parent).IsNull();
        await Assert.That(_character.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(slave.AttachedCharacters).IsEmpty();
    }

    [Test]
    public async Task SlaveRetirement_RejectsReusedObjectIdentity()
    {
        var oldSlave = CreateSlave();
        _world.RemoveObject(oldSlave);
        var replacement = CreateSlave();

        await Assert.That(_world.SlaveManager.TryBeginAttachmentRemoval(oldSlave, out var passengers)).IsFalse();
        await Assert.That(passengers).IsEmpty();
        await Assert.That(replacement.AttachmentsRetired).IsFalse();
    }

    [Test]
    public async Task SlaveRetirement_KeepsCurrentPassengersAvailableForDetachAndRunsOnce()
    {
        var slave = CreateSlave();
        _character.Transform.Parent = slave.Transform;
        _character.AttachedPoint = AttachPointKind.Driver;
        slave.AttachedCharacters[AttachPointKind.Driver] = _character;
        _character.Buffs = Mock.Of<IBuffs>().Object;

        await Assert.That(_world.SlaveManager.TryBeginAttachmentRemoval(slave, out var passengers)).IsTrue();
        await Assert.That(passengers.Single()).IsSameReferenceAs(_character);
        await Assert.That(_world.SlaveManager.TryBeginAttachmentRemoval(slave, out _)).IsFalse();
        _world.SlaveManager.UnbindSlave(_character, slave.TlId, AttachUnitReason.SlaveBinding);

        await Assert.That(_character.Transform.Parent).IsNull();
        await Assert.That(_character.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(slave.AttachedCharacters).IsEmpty();
    }

    private Slave CreateSlave()
    {
        var slave = new Slave { ObjId = 20, TlId = 30, Hp = 100,
            Template = new SlaveTemplate { Mountable = true } };
        SetWorld(slave);
        _world.AddObject(slave);
        return slave;
    }

    private void SetWorld(GameObject unit) =>
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(unit, _world);

    private void RunEntryAfterRetirement(Action enter, Action retire)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { enter(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        try
        {
            lock (_character.AttachmentSyncRoot)
            {
                thread.Start();
                // Entry resolves the mount first, then waits for this character's lock.
                if (!SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The entry request did not reach the attachment lock.");
                retire();
            }
        }
        finally
        {
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The entry request did not finish after retirement.");
        }
        if (failure != null)
            throw new InvalidOperationException("The entry request failed.", failure);
    }

    private sealed class ProbeCharacter() : Character(null)
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }
}
