using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World.Interactions;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public partial class MountSkillAuthorizationTests
{
    [Test]
    [Arguments(75u, 1205u, "hull")]
    [Arguments(75u, 1205u, "wheel")]
    [Arguments(75u, 1205u, "unparented")]
    [Arguments(14u, 128u, "hull")]
    [Arguments(14u, 128u, "wheel")]
    [Arguments(14u, 128u, "unparented")]
    public async Task ShipWheel_PlayerAtWheelCanSteerThenRelease(uint template, uint model, string support)
    {
        var (ship, wheel) = SetUpWheel(template, model);
        ship.Summoner = _actor;
        var offset = Vector3.UnitX * 1.2f;
        switch (support)
        {
            case "hull":
                _actor.Transform.Parent = ship.Transform;
                _actor.Transform.Local.SetPosition(wheel.Transform.Local.Position + offset);
                break;
            case "wheel":
                _actor.Transform.Parent = wheel.Transform;
                _actor.Transform.Local.SetPosition(offset);
                break;
            case "unparented":
                _actor.Transform.Local.SetPosition(wheel.Transform.World.Position + offset);
                break;
        }
        await Assert.That(_actor.AttachedPoint).IsEqualTo(AttachPointKind.None);
        UseWheel(wheel);

        await Assert.That(_actor.AttachedPoint).IsEqualTo(AttachPointKind.Driver);
        await Assert.That(ship.AttachedCharacters[AttachPointKind.Driver]).IsSameReferenceAs(_actor);
        await Assert.That(_actor.Transform.Parent).IsSameReferenceAs(ship.Transform);
        var seat = SlaveGameData.Instance.GetAttachPointsForSlave(model)[AttachPointKind.Driver];
        await Assert.That(_actor.Transform.Local.Position).IsEqualTo(seat.AsPositionVector());

        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _actor };
        var request = new PacketStream().WriteBc(ship.ObjId).Write((byte)MoveTypeEnum.ShipRequest);
        new ShipRequestMoveType { Steering = 1, Throttle = 1 }.Write(request);
        request.Rollback();
        var movement = new CSMoveUnitPacket { Connection = connection };
        movement.Read(request);
        movement.Execute();
        await Assert.That(ship.SteeringRequest).IsEqualTo((sbyte)1);
        await Assert.That(ship.ThrottleRequest).IsEqualTo((sbyte)1);

        var release = new PacketStream().Write(ship.TlId);
        release.Rollback();
        new CSDiscardSlavePacket { Connection = connection }.Read(release);
        await Assert.That(_actor.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(_actor.Transform.Parent).IsNull();
        await Assert.That(ship.AttachedCharacters).IsEmpty();
    }

    [Test]
    [Arguments("another hull")]
    [Arguments("another wheel")]
    [Arguments("passenger")]
    [Arguments("missing driver socket")]
    [Arguments("direct bind")]
    [Arguments("occupied driver")]
    [Arguments("displaced wheel")]
    [Arguments("unbound wheel")]
    [Arguments("detached wheel")]
    [Arguments("retiring wheel")]
    [Arguments("out of range")]
    [Arguments("owners mark")]
    public async Task ShipWheel_RefusalPreservesTheOriginalSupportAndOccupants(string condition)
    {
        var (ship, wheel) = SetUpWheel(75, 1205);
        _actor.Transform.Parent = ship.Transform;
        _actor.Transform.Local.SetPosition(wheel.Transform.Local.Position);
        switch (condition)
        {
            case "another hull":
            case "another wheel":
                var (otherShip, otherWheel) = CreateWheel(75, 1205, 80);
                _actor.Transform.Parent = condition == "another hull" ? otherShip.Transform : otherWheel.Transform;
                _actor.Transform.Local.SetPosition(condition == "another hull" ? wheel.Transform.Local.Position : Vector3.Zero);
                break;
            case "passenger":
                Attach(ship, AttachPointKind.Passenger0);
                _actor.Transform.Local.SetPosition(wheel.Transform.Local.Position);
                break;
            case "occupied driver":
                var driver = new ProbeCharacter { Id = 2, ObjId = 291, Hp = 100, AttachedPoint = AttachPointKind.Driver };
                SetWorld(driver, _world);
                driver.Transform.Parent = ship.Transform;
                ship.AttachedCharacters[AttachPointKind.Driver] = driver;
                break;
            case "missing driver socket":
                SlaveGameData.Instance.GetAttachPointsForSlave(ship.ModelId).Remove(AttachPointKind.Driver);
                break;
            case "displaced wheel":
                wheel.Transform.Local.Translate(Vector3.UnitY);
                break;
            case "unbound wheel":
                ship.AttachedDoodads.Remove(wheel);
                break;
            case "detached wheel":
                wheel.Transform.Parent = null;
                break;
            case "retiring wheel":
                wheel.Despawn = DateTime.UtcNow;
                break;
            case "out of range":
                _actor.Transform.Local.Translate(Vector3.UnitX * 5);
                break;
            case "owners mark":
                ship.Summoner = new Character(null) { ObjId = 291 };
                var buffs = Mock.Of<IBuffs>();
                buffs.CheckBuff((uint)BuffConstants.OwnersMark).Returns(true);
                ship.Buffs = buffs.Object;
                break;
        }
        var support = _actor.Transform.Parent;
        var seat = _actor.AttachedPoint;
        var occupants = ship.AttachedCharacters.ToArray();
        if (condition == "direct bind")
            _world.SlaveManager.BindSlave(_actor, ship.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);
        else
            UseWheel(wheel);
        await Assert.That(_actor.Transform.Parent).IsSameReferenceAs(support);
        await Assert.That(_actor.AttachedPoint).IsEqualTo(seat);
        await Assert.That(ship.AttachedCharacters.SequenceEqual(occupants)).IsTrue();
    }

    private (Slave Ship, Doodad Wheel) SetUpWheel(uint template, uint model)
    {
        SlaveGameData.Instance.LoadSlaveAttachmentPointLocations();
        // Both r208022 ship templates bind wheel 2374 at Helms. Its phase 4873 uses skill 13312.
        var manager = new DoodadManager(null, null, null, null, null);
        var row = new DoodadFunc { FuncKey = 4156, GroupId = 4873, FuncId = 1, FuncType = nameof(DoodadFuncAttachment),
            SkillId = 13312, NextPhase = -1 };
        var action = new DoodadFuncAttachment { AttachPointId = AttachPointKind.Driver, Space = 1 };
        Set(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>> { [4873] = [row] });
        Set(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
            { [nameof(DoodadFuncAttachment)] = new() { [1] = action } });
        Set(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        SetInstance(manager);
        return CreateWheel(template, model, 50);
    }

    private (Slave Ship, Doodad Wheel) CreateWheel(uint template, uint model, ushort id)
    {
        var ship = new Slave { ObjId = id, TlId = id, Hp = 100, Scale = 1, ModelId = model, TemplateId = template,
            Template = new SlaveTemplate { Id = template, ModelId = model, Mountable = true } };
        SetWorld(ship, _world);
        _world.AddObject(ship);
        ship.Transform.Local.SetPosition(100, 100, 90, 0.02f, -0.04f, 1.2f);

        var wheel = new Doodad { ObjId = (uint)id + 1, TemplateId = 2374, Template = new DoodadTemplate { Id = 2374 },
            ParentObjId = ship.ObjId, ParentObj = ship, AttachPoint = AttachPointKind.Helms, FuncGroupId = 4873 };
        SetWorld(wheel, _world);
        wheel.Transform.Parent = ship.Transform;
        wheel.Transform.Local.SetPosition(SlaveGameData.Instance.GetAttachPointsForSlave(model)[AttachPointKind.Helms].AsPositionVector());
        ship.AttachedDoodads.Add(wheel);
        _world.AddObject(wheel);
        return (ship, wheel);
    }

    private void UseWheel(Doodad wheel) => new Use().Execute(_actor, new SkillCasterUnit(_actor.ObjId),
        wheel, new SkillCastDoodadTarget { ObjId = wheel.ObjId }, 13312, 0);
}
