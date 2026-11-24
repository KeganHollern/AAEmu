using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class SlaveRemovalTests
{
    [Test]
    [Arguments(14u, 128u, AttachPointKind.Cannon0)]
    [Arguments(120u, 1385u, AttachPointKind.Sail0)]
    public async Task SpawnedHarpoonCannon_KeepsItsSocketThroughHullMovement(uint templateId, uint modelId, AttachPointKind socket)
    {
        var kind = templateId == 14 ? SlaveKind.Speedboat : SlaveKind.SmallSailingShip;
        var boat = new SlaveTemplate { Id = templateId, ModelId = modelId, SlaveKind = kind, Name = "Hull" };
        var weapon = new SlaveTemplate { Id = 48, ModelId = 895, SlaveKind = SlaveKind.SiegeWeapon, Name = "Harpoon Cannon", Mountable = true };
        boat.SlaveBindings.Add(new SlaveBindings { OwnerType = "Slave", OwnerId = boat.Id, SlaveId = weapon.Id, AttachPointId = socket });
        SetVehicleSpawnServices(boat, weapon);
        SlaveGameData.Instance.LoadSlaveAttachmentPointLocations();
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType> { [modelId] = new() { SubType = "ShipModel", SubId = 3 } });
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>
        {
            ["ShipModel"] = new() { [3] = new ShipModelV1 { Mass = 5000, MassBoxSizeX = 4, MassBoxSizeY = 8, MassBoxSizeZ = 7 } }
        });
        Install(models);
        _world.Physics.Initialize();
        _owner.Id = 0;
        using var pose = new Transform(null);
        pose.Local.SetPosition(100, 100, 10, 0, 0, 0.75f);

        try
        {
            var hull = _world.SlaveManager.Create(_owner, null, boat.Id, positionOverride: pose);
            var cannon = hull.AttachedSlaves.Single();
            var expected = templateId == 14 ? new Vector3(0, 9.05f, 1.315f) : Vector3.Zero;
            await Assert.That(cannon.Transform.Local.Position).IsEqualTo(expected);
            await Assert.That(hull.Transform.Children.Single(child => child.GameObject == cannon)).IsSameReferenceAs(cannon.Transform);

            hull.Transform.Local.SetPosition(104, 99, 10, 0.05f, -0.1f, 1.2f);
            hull.Transform.FinalizeTransform();
            await Assert.That(cannon.Transform.Local.Position).IsEqualTo(expected);
            await Assert.That(cannon.Transform.World.Position).IsEqualTo(hull.Transform.TransformPoint(expected));
            _owner.Transform.Parent = hull.Transform;
            _owner.Transform.Local.SetPosition(expected);
            _world.SlaveManager.BindSlave(_owner.Connection, cannon.TlId);
            await Assert.That(_owner.AttachedPoint).IsEqualTo(templateId == 14 ? AttachPointKind.Driver : AttachPointKind.None);
        }
        finally
        {
            _world.Physics.Dispose();
        }
    }
}
