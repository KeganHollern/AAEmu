using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.Game.Physics;
using AAEmu.Game.Physics.Util;
using AAEmu.Game.Utils;
using Jitter2.LinearMath;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class SkillRangeTests
{
    private readonly List<Action> _restore = [];
    private readonly ShipModelV1 _hull = new() { Id = 2, MassBoxSizeX = 4, MassBoxSizeY = 40, MassBoxSizeZ = 4 };
    private readonly Dictionary<uint, Dictionary<AttachPointKind, WorldSpawnPosition>> _points = [];
    private Unit _caster;
    private Skill _skill;

    [Before(Test)]
    public void SetUp()
    {
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>
        {
            [1] = new() { Id = 1, SubType = "ActorModel", SubId = 1 },
            [2] = new() { Id = 2, SubType = "ShipModel", SubId = 2 }
        });
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>
        {
            ["ActorModel"] = new() { [1] = new ActorModel { Id = 1, Radius = 0.5f } },
            ["ShipModel"] = new() { [2] = _hull }
        });
        Install(models);
        var slaves = new SlaveGameData();
        SetField(slaves, "_attachPoints", _points);
        Install(slaves);
        _caster = new Caster { ModelId = 1 };
        _skill = new Skill(new SkillTemplate { Id = 1, MaxRange = 3, TargetType = SkillTargetType.Doodad });
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var restore in _restore)
            restore();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Check_DistantObject_RejectsDoodadsAndVehicles(bool vehicle)
    {
        BaseUnit target = vehicle ? new Slave() : new Doodad();
        target.Transform.Local.Position = new Vector3(100, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.TooFarRange);
        target.Transform.Local.Position = new Vector3(3.5f, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.Success);
        target.Transform.Local.Position = new Vector3(3.51f, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task Check_MinimumRangeAndHeight_RemainEnforced()
    {
        var target = new Doodad();
        _skill.Template.MinRange = 1;
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.TooCloseRange);
        target.Transform.Local.Position = new Vector3(0, 0, 5);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task Check_RemoveStone_PreservesCompactMinimumCorrection()
    {
        _skill.Template.MinRange = 100;
        _skill.Template.MaxRange = 200;
        var target = new Doodad();
        target.Transform.Local.Position = new Vector3(2, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    public async Task Check_ShipBow_UsesHullEdgeInsteadOfPivot()
    {
        var ship = new Slave { ModelId = 2 };
        _caster.Transform.Local.Position = new Vector3(0, 22, 0);
        await Assert.That(SkillRange.GetDistance(_caster, ship)).IsEqualTo(1.5f);
        await Assert.That(SkillRange.Check(_skill, _caster, ship)).IsEqualTo(SkillResult.Success);
        _caster.Transform.Local.Position = new Vector3(0, 24, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, ship)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task Check_RotatedShip_UsesOrientedHull()
    {
        var ship = new Slave { ModelId = 2 };
        ship.Transform.Local.Rotation = new Vector3(0, 0, MathF.PI / 2);
        _caster.Transform.Local.Position = new Vector3(22, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, ship)).IsEqualTo(SkillResult.Success);
        _caster.Transform.Local.Position = new Vector3(0, 22, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, ship)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task Check_ScaledOffsetHull_UsesWorldTranslationAndModelCenter()
    {
        var ship = new Slave { ModelId = 2, Scale = 2 };
        ship.Transform.Local.Position = new Vector3(100, 100, 20);
        _hull.MassCenterY = -4;
        _caster.Transform.Local.Position = new Vector3(100, 134, 20);
        await Assert.That(SkillRange.GetDistance(_caster, ship)).IsEqualTo(1.5f);
        _caster.Transform.Local.Position = new Vector3(100, 136, 20);
        await Assert.That(SkillRange.Check(_skill, _caster, ship)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task Check_ShipVerticalRange_DoesNotUseHorizontalDistanceAlone()
    {
        var ship = new Slave { ModelId = 2 };
        _caster.Transform.Local.Position = new Vector3(0, 0, 8);
        await Assert.That(SkillRange.GetDistance(_caster, ship)).IsEqualTo(5.5f);
        await Assert.That(SkillRange.Check(_skill, _caster, ship)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task GetDistance_HullCorner_UsesNearestPoint()
    {
        var ship = new Slave { ModelId = 2 };
        _caster.Transform.Local.Position = new Vector3(4, 22, 0);
        await Assert.That(Math.Abs(SkillRange.GetDistance(_caster, ship) - (MathF.Sqrt(8) - 0.5f)) < 0.0001f).IsTrue();
    }

    [Test]
    public async Task Check_RepairPoint_UsesAttachmentPositionAndFollowsShipMovement()
    {
        var ship = new Slave { ModelId = 2, Scale = 2 };
        var repair = new Doodad { ParentObj = ship, AttachPoint = AttachPointKind.HealPoint0 };
        _points[2] = new() { [AttachPointKind.HealPoint0] = new WorldSpawnPosition { X = 0, Y = 10, Z = 1 } };
        _caster.Transform.Local.Position = new Vector3(0, 22, 2);
        await Assert.That(SkillRange.Check(_skill, _caster, repair)).IsEqualTo(SkillResult.Success);
        _caster.Transform.Local.Position = new Vector3(0, -20, 2);
        await Assert.That(SkillRange.Check(_skill, _caster, repair)).IsEqualTo(SkillResult.TooFarRange);
        ship.Transform.Local.Position = new Vector3(100, 0, 0);
        ship.Transform.Local.Rotation = new Vector3(0, 0, MathF.PI / 2);
        _caster.Transform.Local.Position = new Vector3(78, 0, 2);
        await Assert.That(SkillRange.Check(_skill, _caster, repair)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(0f)]
    [Arguments(75f)]
    [Arguments(90f)]
    [Arguments(-150f)]
    public async Task ClipperRepairPoint_FollowsTheDisplayedDeckBeforeAndAfterAMovementUpdate(float heading)
    {
        var ship = new Slave { ModelId = 2 };
        var origin = new Vector3(12845, 13922, 100);
        ship.Transform.Local.Position = origin;
        var socket = new Vector3(-0.621f, 3.775f, 1.229f); // Model 128, HealPoint1.
        _points[2] = new() { [AttachPointKind.HealPoint1] = new WorldSpawnPosition { X = socket.X, Y = socket.Y, Z = socket.Z } };
        var repair = new Doodad { ParentObj = ship, AttachPoint = AttachPointKind.HealPoint1 };
        var radians = heading * MathF.PI / 180;
        var body = JQuaternion.CreateRotationY(radians);
        var expected = origin + Vector3.Transform(socket, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians));
        var rep = new ReplicationSmoothing();
        var move = new ShipMoveType();
        _caster.Transform.Local.Position = expected;

        PhysicsManager.ApplyDeckRotation(ship.Transform.Local, body, rep, move);
        await Assert.That(Vector3.Distance(expected, origin + Vector3.Transform(socket, ship.Transform.Local.ToQuaternion()))).IsLessThan(0.003f);
        await Assert.That(SkillRange.GetDistance(_caster, repair)).IsEqualTo(0f);
        await Assert.That(move.RotationX).IsEqualTo((short)0);
        await Assert.That(move.RotationY).IsEqualTo((short)0);
        await Assert.That(Math.Abs(move.RotationZ * 0.00003052f - MathF.Sin(radians / 2))).IsLessThan(0.0001f);

        PhysicsManager.ApplyDeckRotation(ship.Transform.Local, body, rep);
        await Assert.That(SkillRange.GetDistance(_caster, repair)).IsEqualTo(0f);
        _caster.Transform.Local.Position = expected + Vector3.UnitZ * 10;
        await Assert.That(SkillRange.Check(_skill, _caster, repair)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task DeckRotation_PreservesThePublishedBankAndWavePitchAcrossPhysicsTicks()
    {
        var body = JQuaternion.CreateRotationY(1.3f);
        var rep = new ReplicationSmoothing { BankSmoothed = 0.1f, GroundPitchSmoothed = 0.03f, WavePitch = -0.01f };
        var (heading, roll, pitch) = PhysicsUtil.GetYawPitchRollFromMatrix(JMatrix.CreateFromQuaternion(body));
        var (z, y, x) = MathUtil.GetSlaveRotationFromDegrees(heading, roll + rep.BankSmoothed, pitch + rep.GroundPitchSmoothed + rep.WavePitch);
        var move = new ShipMoveType();
        var transform = new PositionAndRotation();
        PhysicsManager.ApplyDeckRotation(transform, body, rep, move);
        await Assert.That((move.RotationX, move.RotationY, move.RotationZ)).IsEqualTo((x, y, z));

        var xyz = new Vector3(x, y, z) * 0.00003052f;
        var wire = new Quaternion(xyz, MathF.Sqrt(1 - xyz.LengthSquared()));
        PhysicsManager.ApplyDeckRotation(transform, body, rep);
        await Assert.That(MathF.Abs(Quaternion.Dot(transform.ToQuaternion(), wire))).IsGreaterThan(0.99999f);
    }

    [Test]
    public async Task ShipRepair_FollowsItsStartingFootprintThroughContactChangesAndValidatesTheSamePoint()
    {
        var skill = new Skill(new SkillTemplate { Id = SkillsEnum.RepairShip, MaxRange = 4, TargetType = SkillTargetType.Doodad });
        var ship = new Slave { ModelId = 2 };
        ship.Transform.Local.Position = new Vector3(12880, 13930, 100);
        var socket = new Vector3(-0.621f, 3.775f, 1.229f);
        _points[2] = new() { [AttachPointKind.HealPoint1] = new WorldSpawnPosition { X = socket.X, Y = socket.Y, Z = socket.Z } };
        var point = new Doodad { ParentObj = ship, AttachPoint = AttachPointKind.HealPoint1 };
        ship.AttachedDoodads.Add(point);
        _caster.Transform.Parent = ship.Transform;
        _caster.Transform.Local.Position = socket;
        var start = _caster.Transform.World.Position;
        var task = new CastTask(skill, _caster, null, point, null, null)
        {
            CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(14), true, false, true)
        };
        _caster.SkillTask = task;
        _caster.Transform.Parent = null;
        _caster.Transform.Local.Position = start + new Vector3(-0.2f, 0.1f, 0);
        SkillCastReactions.OnMovement(_caster, socket, _caster.Transform.Local.Position, false, false);
        await Assert.That(task.CastWindow.Active).IsTrue();
        _caster.Transform.Parent = ship.Transform;
        SkillCastReactions.OnMovement(_caster, start, _caster.Transform.Local.Position, false, false);
        await Assert.That(task.CastWindow.Active).IsTrue();

        // Rotation and travel carry both the point and the caster to a new world position.
        ship.Transform.Local.Position += new Vector3(-35, 7, 0);
        ship.Transform.Local.SetRotation(0, 0, 1.3f);
        await Assert.That(task.Repair.CanContinue(_caster)).IsTrue();
        // Small individual steps must still accumulate relative to the original cast position.
        _caster.Transform.Local.Position = socket + Vector3.UnitX * 0.4f;
        await Assert.That(task.Repair.CanContinue(_caster)).IsTrue();
        _caster.Transform.Local.Position += Vector3.UnitX * 0.2f;
        await Assert.That(task.Repair.CanContinue(_caster)).IsFalse();
        _caster.Transform.Local.Position = socket;
        _caster.Transform.Parent = null;
        ship.Transform.Local.Position += Vector3.UnitY * 10;
        await Assert.That(task.Repair.CanContinue(_caster)).IsFalse();
        _caster.Transform.Parent = ship.Transform;
        _caster.Transform.Local.Position = socket;
        ship.AttachedDoodads.Remove(point);
        await Assert.That(task.Repair.CanContinue(_caster)).IsFalse();
        await Assert.That(new CastTask(_skill, _caster, null, point, null, null).Repair).IsNull();
    }

    [Test]
    public async Task Check_MissingAttachment_UsesBoundedHullInsteadOfWorldOrigin()
    {
        var ship = new Slave { ModelId = 2 };
        ship.Transform.Local.Position = new Vector3(100, 100, 20);
        var repair = new Doodad { ParentObj = ship, AttachPoint = AttachPointKind.HealPoint0 };
        await Assert.That(SkillRange.Check(_skill, _caster, repair)).IsEqualTo(SkillResult.TooFarRange);
        _caster.Transform.Local.Position = new Vector3(100, 122, 20);
        await Assert.That(SkillRange.Check(_skill, _caster, repair)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    public async Task Check_FreePlacedDoodad_DoesNotUseTheOwningHull()
    {
        var ship = new Slave { ModelId = 2 };
        var decoration = new Doodad { ParentObj = ship, AttachPoint = AttachPointKind.None };
        decoration.Transform.Local.Position = new Vector3(100, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, decoration)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task Check_NonFinitePosition_DoesNotPassRange()
    {
        var target = new Doodad();
        target.Transform.Local.Position = new Vector3(float.NaN, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.TooFarRange);
    }

    [Test]
    public async Task Check_WeaponRange_OverridesSkillRangeForDoodads()
    {
        _skill.Template.MaxRange = 100;
        _skill.Template.WeaponSlotForRangeId = 1;
        _caster.Equipment.Items.Add(new Item
        {
            Slot = 1,
            Template = new WeaponTemplate { HoldableTemplate = new Holdable { MinRange = 2, MaxRange = 5 } }
        });
        var target = new Doodad();
        target.Transform.Local.Position = new Vector3(6, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.TooFarRange);
        target.Transform.Local.Position = new Vector3(5, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.Success);
        target.Transform.Local.Position = new Vector3(1, 0, 0);
        await Assert.That(SkillRange.Check(_skill, _caster, target)).IsEqualTo(SkillResult.TooCloseRange);
    }

    [Test]
    public async Task Check_RangeModifier_AppliesToDoodads()
    {
        var caster = new Caster { ModelId = 1, RangeBonus = 2 };
        var target = new Doodad();
        target.Transform.Local.Position = new Vector3(5.5f, 0, 0);
        await Assert.That(SkillRange.Check(_skill, caster, target)).IsEqualTo(SkillResult.Success);
        caster.RangeBonus = 0;
        await Assert.That(SkillRange.Check(_skill, caster, target)).IsEqualTo(SkillResult.TooFarRange);
    }

    private void Install<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        _restore.Add(() => field.SetValue(null, previous));
        field.SetValue(null, value);
    }

    private static void SetField(object owner, string name, object value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private sealed class Caster : Unit
    {
        public double RangeBonus { get; set; }
        public override double ApplySkillModifiers(Skill skill, SkillAttribute attribute, double baseValue) =>
            attribute == SkillAttribute.Range ? baseValue + RangeBonus : baseValue;
    }
}
