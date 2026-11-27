using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class SlaveRemovalTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task LandEscape_PublishesTheSameCartAtTheNewPose(bool driverSeated, bool driverMoving)
    {
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        Install(models);
        Install(new ZoneManager(null, null));
        using var movement = new SusManager(WorldManager.Instance) { WriteBatch = _ => { } };
        Install(movement);

        // Only the empty-cart case crosses a region. It has a departing and an arriving observer.
        const int sector = WorldManager.REGION_SIZE;
        var before = new Vector3(4 * sector - 2, 12, 10);
        var after = before + new Vector3(driverSeated ? -3 : 5, 0, 0);
        _slave.Template = new SlaveTemplate { Id = 555, ModelId = 8000009, Mountable = true, SlaveKind = SlaveKind.Machine };
        _slave.TemplateId = _slave.Template.Id;
        _slave.ModelId = _slave.Template.ModelId;
        _slave.Name = "Farm Cart";
        var scroll = SetSavedSummonLocation();
        _slave.Region.RemoveObject(_slave);
        Place(_slave, before);
        Place(_owner, before - Vector3.UnitY);
        var nearby = Watch(31, before + Vector3.UnitY);
        var departure = Watch(32, new Vector3(sector + 1, 12, 10));
        var arrival = Watch(33, new Vector3(6 * sector + 1, 12, 10));

        var rack = new Doodad { ObjId = 80, DbId = 81, TemplateId = 3446, ItemId = 82, ParentWorld = _world,
            ParentObj = _slave, ParentObjId = _slave.ObjId, AttachPoint = AttachPointKind.Cannon0, IsVisible = true };
        rack.Transform.Parent = _slave.Transform;
        rack.Transform.Local.SetPosition(0, 1, 1);
        _slave.AttachedDoodads.Add(rack);
        _world.AddObject(rack);
        WorldManager.Instance.AddVisibleObject(rack);
        if (driverSeated)
            _world.SlaveManager.BindSlave(nearby.Player, _slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);
        if (driverMoving)
        {
            Drive(before - Vector3.UnitX * .1f, velocity: -2500);
            await Assert.That(_slave.VehicleVelocity.X).IsLessThan(0);
        }
        foreach (var session in new[] { _session, nearby.Session, departure.Session, arrival.Session })
            session.Packets.Clear();
        _owner.CurrentTarget = _slave;
        nearby.Player.CurrentTarget = _slave;

        _world.SlaveManager.PlacementGeometry = (_, _, _) => ErrorMessageType.NoErrorMessage;
        var target = new SkillCastPositionTarget { PosX = after.X, PosY = after.Y, PosZ = after.Z, PosRot = 1.5f * MathF.PI };
        await Assert.That(_world.SlaveManager.MoveEscapingVehicle(_owner, _slave, target)).IsTrue();

        foreach (var session in new[] { _session, nearby.Session })
        {
            if (!driverSeated || !ReferenceEquals(session, nearby.Session))
                await Assert.That(session.Packets.Count).IsEqualTo(1);
            var packet = session.Packets.Single(p => BitConverter.ToUInt16(p, 6) == SCOffsets.SCOneUnitMovementPacket);
            var body = new PacketStream(packet[8..]);
            await Assert.That(body.ReadBc()).IsEqualTo(_slave.ObjId);
            await Assert.That(body.ReadByte()).IsEqualTo((byte)MoveTypeEnum.Vehicle);
            var pose = new VehicleMoveType();
            pose.Read(body);
            await Assert.That(Vector3.Distance(new Vector3(pose.X, pose.Y, pose.Z), after)).IsLessThan(.001f);
            var (_, _, yaw) = MathUtil.GetSlaveRotationInDegrees(pose.RotationX, pose.RotationY, pose.RotationZ);
            await Assert.That(Math.Abs(MathF.IEEERemainder(yaw - target.PosRot, MathF.Tau))).IsLessThan(.001f);
        }
        await Assert.That(nearby.Player.CurrentTarget).IsSameReferenceAs(_slave);
        await Assert.That(_owner.CurrentTarget).IsSameReferenceAs(_slave);
        await Assert.That(_slave.SummoningItem).IsSameReferenceAs(scroll);
        await Assert.That(scroll.SlaveDbId).IsEqualTo(_slave.Id);
        await Assert.That(rack.ItemId).IsEqualTo(82ul);
        await Assert.That(rack.Transform.Parent).IsSameReferenceAs(_slave.Transform);
        await Assert.That(Vector3.Distance(rack.Transform.World.Position, after + new Vector3(1, 0, 1))).IsLessThan(.001f);

        if (driverSeated)
        {
            await Assert.That(nearby.Player.Transform.Parent).IsSameReferenceAs(_slave.Transform);
            await Assert.That(_slave.AttachedCharacters[AttachPointKind.Driver]).IsSameReferenceAs(nearby.Player);
            var types = nearby.Session.Packets.Select(p => BitConverter.ToUInt16(p, 6)).ToList();

            // The ordinary idle report arrived 130 ms after the field relocation. Another
            // report can still be in transit if the cart was already moving at departure.
            Drive(before);
            Drive(before - Vector3.UnitX * .2f, velocity: -2500);
            await Assert.That(_slave.VehicleVelocity).IsEqualTo(Vector3.Zero);
            await Assert.That(_slave.Transform.World.Position).IsEqualTo(after);
            await Assert.That(nearby.Player.Transform.Parent).IsSameReferenceAs(_slave.Transform);
            var remove = types.IndexOf(SCOffsets.SCUnitsRemovedPacket);
            var state = types.IndexOf(SCOffsets.SCUnitStatePacket);
            var attach = types.IndexOf(SCOffsets.SCUnitAttachedPacket);
            var bound = types.IndexOf(SCOffsets.SCSlaveBoundPacket);
            await Assert.That(remove).IsGreaterThanOrEqualTo(0);
            await Assert.That(state).IsGreaterThan(remove);
            await Assert.That(attach).IsGreaterThan(state);
            await Assert.That(bound).IsGreaterThan(attach);
            Drive(after + Vector3.UnitY * .25f);
            await Assert.That(Vector3.Distance(_slave.Transform.World.Position, after + Vector3.UnitY * .25f)).IsLessThan(.001f);
            // After the first accepted arrival, the driver can go back towards the original site.
            Drive(before);
            await Assert.That(Vector3.Distance(_slave.Transform.World.Position, before)).IsLessThan(.001f);

            await Assert.That(_world.SlaveManager.MoveEscapingVehicle(_owner, _slave, target)).IsTrue();
            Drive(before);
            await Assert.That(_slave.Transform.World.Position).IsEqualTo(after);
            _world.SlaveManager.UnbindSlave(nearby.Player, _slave.TlId, AttachUnitReason.None);
            await Assert.That(nearby.Player.Transform.Parent).IsNull();
            Drive(before);
            await Assert.That(_slave.Transform.World.Position).IsEqualTo(after);
            var displaced = after + Vector3.UnitX * .1f;
            var replacement = Watch(34, after);
            _world.SlaveManager.BindSlave(replacement.Player, _slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);
            Drive(displaced); // A late move from the original driver has no effect.
            await Assert.That(_slave.Transform.World.Position).IsEqualTo(after);
            Drive(displaced, replacement.Player);
            await Assert.That(Vector3.Distance(_slave.Transform.World.Position, displaced)).IsLessThan(.001f);
        }
        else
        {
            var oldTypes = departure.Session.Packets.Select(p => BitConverter.ToUInt16(p, 6)).ToArray();
            await Assert.That(oldTypes).Contains(SCOffsets.SCUnitsRemovedPacket);
            await Assert.That(oldTypes).DoesNotContain(SCOffsets.SCOneUnitMovementPacket);
            var newTypes = arrival.Session.Packets.Select(p => BitConverter.ToUInt16(p, 6)).ToArray();
            var entry = Array.IndexOf(newTypes, SCOffsets.SCSlaveStatePacket);
            await Assert.That(entry).IsGreaterThanOrEqualTo(0);
            await Assert.That(Array.IndexOf(newTypes, SCOffsets.SCOneUnitMovementPacket)).IsGreaterThan(entry);
        }

        void Drive(Vector3 position, Character driver = null, short velocity = 0)
        {
            var body = new PacketStream().WriteBc(_slave.ObjId).Write((byte)MoveTypeEnum.Vehicle);
            new VehicleMoveType { X = position.X, Y = position.Y, Z = position.Z, VelX = velocity }.Write(body);
            body.Rollback();
            var movementPacket = new CSMoveUnitPacket { Connection = (driver ?? nearby.Player).Connection };
            movementPacket.Read(body);
            movementPacket.Execute();
        }

        void Place(GameObject obj, Vector3 position)
        {
            obj.Transform.Local.SetPosition(position);
            var region = _world.GetRegionByPos(position);
            region.AddObject(obj);
            obj.Region = region;
        }

        (Character Player, RecordingSession Session) Watch(uint id, Vector3 position)
        {
            var session = new RecordingSession();
            var player = new RecordingCharacter { Id = id, ObjId = id, Hp = 100, ParentWorld = _world,
                Connection = new GameConnection(session) };
            player.Connection.ActiveChar = player;
            _world.AddObject(player);
            WorldManager.Instance.TryAddCharacter(player);
            Place(player, position);
            return (player, session);
        }
    }
}
