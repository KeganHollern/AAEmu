using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Skills;
using Jitter2.LinearMath;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RidersEscape_KeepsTheOriginalHullAndRackAtTheAcceptedWaterPose(bool obstructed)
    {
        SetInstance(new TaskManager(null));
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new SkillRequirementsGameData());
        AppConfiguration.Instance.World ??= new WorldConfig();
        var models = new ModelManager();
        const uint model = 128;
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType> { [model] = new() { SubType = "ShipModel", SubId = 1 } });
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>> {
            ["ShipModel"] = new() { [1] = new ShipModelV1 { Mass = 100, MassBoxSizeX = 2, MassBoxSizeY = 6, MassBoxSizeZ = 2 } } });
        SetInstance(models);
        var (world, ship, scroll) = PrepareVehiclePlacementBoundary();
        world.AddObject(_owner);
        _owner.Region.AddObject(_owner);
        world.Water = new WaterBodies { OceanLevel = 10 };
        var physics = new PhysicsManager { SimulationWorld = world };
        SetField(world, "<Physics>k__BackingField", physics);
        physics.Initialize();
        try
        {
            ship.ModelId = model;
            ship.Template = new SlaveTemplate { Id = 14, ModelId = model, SlaveKind = SlaveKind.SmallSailingShip };
            ship.TemplateId = 14;
            var session = new RankFeedbackSession();
            _owner.Connection = new GameConnection(session) { ActiveChar = _owner };
            physics.AddShip(ship);
            var hull = ship.RigidBody;
            var before = ship.Transform.World.Position;
            var initialHp = ship.Hp;
            var rack = new Doodad { ObjId = 80, DbId = 81, ItemId = 82, ParentObj = ship, ParentWorld = world };
            ship.AttachedDoodads.Add(rack);
            rack.Transform.Parent = ship.Transform;
            rack.Transform.Local.Position = Vector3.UnitX;
            var selected = new SkillCastPositionTarget { PosX = 104, PosY = 105, PosZ = 10, PosRot = MathF.PI / 2 };
            var after = new Vector3(selected.PosX, selected.PosY, selected.PosZ);
            var update = typeof(PhysicsManager).GetMethod("SendUpdatedMovementData", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Publish() => update.Invoke(physics, [ship, hull, TimeSpan.FromMilliseconds(50)]);
            Publish();
            session.Packets.Clear();
            world.SlaveManager.PlacementGeometry = (_, _, _) => obstructed
                ? ErrorMessageType.SlaveSpawnShipNeedMoreSpace : ErrorMessageType.NoErrorMessage;
            var skill = new Skill(CreateOwnerEscapeTemplate());
            var result = skill.Use(_owner, new SkillCasterUnit(_owner.ObjId), selected, new SkillObject(), true, out _);
            if (obstructed)
            {
                await Assert.That(result).IsNotEqualTo(SkillResult.Success);
                await Assert.That(_owner.Cooldowns.CheckCooldown(skill.Template)).IsFalse();
                await Assert.That(ship.Transform.World.Position).IsEqualTo(before);
                return;
            }

            await Assert.That(result).IsEqualTo(SkillResult.Success);
            var cast = (CastTask)_owner.SkillTask;
            cast.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(-1), true, false, false);
            // Shore and collision forces can remain pending after the last physics step.
            hull.AddForce(new JVector(36000f, 0f, 0f), hull.Position + JVector.UnitZ);
            await Assert.That(hull.Force.X).IsGreaterThan(0f);
            await Assert.That(MathF.Abs(hull.Torque.Y)).IsGreaterThan(0f);
            ship.StaticObstacleHullDamageContactActive = true;
            ship.StaticObstacleHullDamageSecondsAccumulator = 1f;
            cast.Execute();
            var packet = session.Packets.Single(p => BitConverter.ToUInt16(p, 6) == SCOffsets.SCOneUnitMovementPacket);
            var message = new PacketStream(packet[8..]);
            await Assert.That(message.ReadBc()).IsEqualTo(ship.ObjId);
            await Assert.That(message.ReadByte()).IsEqualTo((byte)MoveTypeEnum.Ship);
            var movement = new ShipMoveType();
            movement.Read(message);
            await Assert.That(Vector3.Distance(new(movement.X, movement.Y, movement.Z), after)).IsLessThan(.001f);

            // The source contact cannot accelerate the hull at its new location.
            await Assert.That(hull.Force.X).IsEqualTo(0f);
            await Assert.That(hull.Torque.Y).IsEqualTo(0f);
            physics._physWorld.Step(.005f, false);
            await Assert.That(MathF.Abs(hull.Velocity.X)).IsLessThan(.001f);
            await Assert.That(MathF.Abs(hull.AngularVelocity.Y)).IsLessThan(.001f);
            Publish();
            var sent = ship.ShipController.Replication;
            await Assert.That(Vector3.Distance(new(sent.PosX, sent.PosY, sent.PosZ), after)).IsLessThan(.001f);
            await Assert.That(Vector3.Distance(new(hull.Position.X, hull.Position.Z, hull.Position.Y), after)).IsLessThan(.001f);
            await Assert.That(Vector3.Distance(ship.Transform.World.Position, after)).IsLessThan(.001f);
            await Assert.That(MathF.Abs(ship.Transform.World.Rotation.Z - selected.PosRot)).IsLessThan(.001f);
            await Assert.That(ship.RigidBody).IsSameReferenceAs(hull);
            await Assert.That(world.SlaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsSameReferenceAs(ship);
            await Assert.That(ship.SummoningItem).IsSameReferenceAs(scroll);
            await Assert.That(scroll.SlaveDbId).IsEqualTo(ship.Id);
            await Assert.That(ship.Hp).IsEqualTo(initialHp);
            await Assert.That(ship.StaticObstacleHullDamageContactActive).IsFalse();
            await Assert.That(ship.StaticObstacleHullDamageSecondsAccumulator).IsEqualTo(0f);
            await Assert.That(rack.Transform.Parent).IsSameReferenceAs(ship.Transform);
            await Assert.That(rack.ItemId).IsEqualTo(82ul);
            await Assert.That(Vector3.Distance(rack.Transform.World.Position, after + Vector3.UnitY)).IsLessThan(.001f);
        }
        finally
        {
            physics.RemoveShip(ship);
            physics.Dispose();
        }
    }
}
