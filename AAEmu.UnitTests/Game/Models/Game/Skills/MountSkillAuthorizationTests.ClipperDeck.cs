using System.Numerics;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public partial class MountSkillAuthorizationTests
{
    [Test]
    [Arguments(14u, 128u, "deck", true)]
    [Arguments(14u, 128u, "cannon", true)]
    [Arguments(14u, 128u, "dock", true)]
    [Arguments(110u, 1360u, "deck", true)]
    [Arguments(113u, 1383u, "deck", true)]
    [Arguments(114u, 1384u, "deck", true)]
    [Arguments(120u, 1385u, "deck", false)]
    [Arguments(120u, 1385u, "cannon", false)]
    [Arguments(120u, 1385u, "dock", true)]
    [Arguments(14u, 128u, "another deck", false)]
    [Arguments(14u, 128u, "another child", false)]
    [Arguments(14u, 128u, "occupied", false)]
    [Arguments(14u, 128u, "too far", false)]
    [Arguments(14u, 128u, "missing binding", false)]
    [Arguments(14u, 128u, "displaced child", false)]
    [Arguments(14u, 128u, "retired hull", false)]
    public async Task Ship_HarpoonSeatUsesTheDeckOfItsOwnShip(uint template, uint model, string start, bool canMount)
    {
        SlaveGameData.Instance.LoadSlaveAttachmentPointLocations();
        var kind = template switch { 14 => SlaveKind.Speedboat, 120 => SlaveKind.SmallSailingShip, _ => SlaveKind.Fishboat };
        var ship = new Slave
        {
            Id = 7, ObjId = 50, TlId = 50, Hp = 100, Scale = 1, ModelId = model, TemplateId = template,
            Template = new SlaveTemplate { Id = template, ModelId = model, Mountable = true, SlaveKind = kind },
            Summoner = _actor
        };
        SetWorld(ship, _world);
        ship.Transform.Local.SetPosition(100, 100, 90, 0.02f, -0.04f, 1.2f);
        _world.AddObject(ship);
        // The stock wreck display binds the same child at Sail0 but has no captured attachment map.
        var attachPoint = template == 120 ? AttachPointKind.Sail0 : AttachPointKind.Cannon0;
        ship.Template.SlaveBindings.Add(new SlaveBindings { OwnerId = template, OwnerType = "Slave",
            SlaveId = 48, AttachPointId = attachPoint });
        var cannon = new Slave
        {
            Id = 8, ObjId = 55, TlId = 55, TemplateId = 48, ModelId = 895, Hp = 100, Scale = 1,
            Template = new SlaveTemplate { Id = 48, ModelId = 895, Mountable = true, SlaveKind = SlaveKind.SiegeWeapon },
            ParentObj = ship, Summoner = _actor, OwnerType = BaseUnitType.Slave, OwnerObjId = ship.ObjId,
            OwnerId = ship.Id, AttachPointId = (sbyte)attachPoint
        };
        SetWorld(cannon, _world);
        cannon.Transform.Parent = ship.Transform;
        var socket = SlaveGameData.Instance.GetAttachPointsForSlave(model)?.GetValueOrDefault(attachPoint);
        if (socket != null)
            cannon.Transform.Local.SetPosition(socket.X, socket.Y, socket.Z, socket.Roll, socket.Pitch, socket.Yaw);
        else
            cannon.Transform.Local.SetPosition(Vector3.Zero);
        ship.AttachedSlaves.Add(cannon);
        _world.AddObject(cannon);

        // The same local spot on the owned ship can be within reach before or after the client reports deck support.
        var deckPosition = cannon.Transform.Local.Position + Vector3.UnitX;
        _actor.Transform.Parent = ship.Transform;
        _actor.Transform.Local.SetPosition(deckPosition);
        var worldPosition = _actor.Transform.World.Position;
        switch (start)
        {
            case "cannon":
                _actor.Transform.Parent = cannon.Transform;
                break;
            case "another child":
                var sibling = new Slave { ObjId = 85, TlId = 85, Hp = 100, Scale = 1, ParentObj = ship };
                SetWorld(sibling, _world);
                sibling.Transform.Parent = ship.Transform;
                ship.AttachedSlaves.Add(sibling);
                _actor.Transform.Parent = sibling.Transform;
                break;
            case "dock":
                _actor.Transform.Parent = null;
                break;
            case "another deck":
                var neighbor = new Slave { ObjId = 80, TlId = 80, Hp = 100, Scale = 1, ModelId = model,
                    TemplateId = template, Template = ship.Template };
                SetWorld(neighbor, _world);
                neighbor.Transform.Local.SetPosition(ship.Transform.Local.Position);
                _world.AddObject(neighbor);
                _actor.Transform.Parent = neighbor.Transform;
                break;
            case "occupied":
                cannon.AttachedCharacters.Add(AttachPointKind.Driver, new Character(null) { Id = 3, ObjId = 88 });
                break;
            case "too far":
                _actor.Transform.Local.Translate(Vector3.UnitX * 4);
                break;
            case "missing binding":
                ship.Template.SlaveBindings.Clear();
                break;
            case "displaced child":
                cannon.Transform.Local.Translate(Vector3.UnitX * 0.5f);
                break;
            case "retired hull":
                ship.AttachmentsRetired = true;
                break;
        }
        if (start != "too far")
            await Assert.That(Vector3.Distance(worldPosition, _actor.Transform.World.Position)).IsLessThan(0.001f);
        var support = _actor.Transform.Parent;
        var occupants = cannon.AttachedCharacters.ToArray();
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _actor };
        _world.SlaveManager.BindSlave(connection, cannon.TlId);
        if (!canMount)
        {
            await Assert.That(_actor.AttachedPoint).IsEqualTo(AttachPointKind.None);
            await Assert.That(_actor.Transform.Parent).IsSameReferenceAs(support);
            await Assert.That(cannon.AttachedCharacters.SequenceEqual(occupants)).IsTrue();
            return;
        }
        await Assert.That(_actor.AttachedPoint).IsEqualTo(AttachPointKind.Driver);
        await Assert.That(_actor.Transform.Parent).IsSameReferenceAs(cannon.Transform);
        await Assert.That(cannon.AttachedCharacters[AttachPointKind.Driver]).IsSameReferenceAs(_actor);
        await Assert.That(ship.AttachedCharacters).IsEmpty();
        _world.SlaveManager.UnbindSlave(_actor, cannon.TlId, AttachUnitReason.SlaveBinding);
        await Assert.That(_actor.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(cannon.AttachedCharacters).IsEmpty();
    }
}
