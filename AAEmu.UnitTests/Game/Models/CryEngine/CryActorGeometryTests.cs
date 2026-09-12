using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Models;

namespace AAEmu.UnitTests.Game.Models.CryEngine;

public sealed class CryActorGeometryTests
{
    [Test]
    public async Task Cylinder_UsesStanceRadiusHalfHeightAndPivot()
    {
        var asset = CryActorGeometry.Create(Stance(false), 1);
        var shape = (CryCylinder)asset.Parts.Single().Shape;
        await Assert.That(shape.Radius).IsEqualTo(0.5f);
        await Assert.That(shape.HalfHeight).IsEqualTo(0.75f);
        await Assert.That(shape.Center).IsEqualTo(new Vector3(0, 0, 1));
        await Assert.That(shape.IsCapsule).IsFalse();
        await Assert.That(asset.Bounds.Min).IsEqualTo(new Vector3(-0.5f, -0.5f, 0.25f));
        await Assert.That(asset.Bounds.Max).IsEqualTo(new Vector3(0.5f, 0.5f, 1.75f));
    }

    [Test]
    public async Task Capsule_PreservesCylinderHalfHeightAndAddsEndcaps()
    {
        var asset = CryActorGeometry.Create(Stance(true), 2);
        var shape = (CryCylinder)asset.Parts.Single().Shape;
        await Assert.That(shape.Radius).IsEqualTo(1);
        await Assert.That(shape.HalfHeight).IsEqualTo(1.5f);
        await Assert.That(shape.Center).IsEqualTo(new Vector3(0, 0, 2));
        await Assert.That(shape.IsCapsule).IsTrue();
        await Assert.That(asset.Bounds.Min).IsEqualTo(new Vector3(-1, -1, -0.5f));
        await Assert.That(asset.Bounds.Max).IsEqualTo(new Vector3(1, 1, 4.5f));
    }

    [Test]
    public async Task ModelAndViewOffsets_DoNotMoveTheLivingCollider()
    {
        var stance = Stance(false);
        var expected = CryActorGeometry.Create(stance, 1).Bounds;
        stance.ModelOffset = new Vector3(50, 60, 70);
        stance.ViewOffset = new Vector3(10, 20, 30);
        stance.Size = stance.Size with { Y = 12 };
        await Assert.That(CryActorGeometry.Create(stance, 1).Bounds).IsEqualTo(expected);
    }

    private static GameStance Stance(bool capsule) => new()
    {
        Size = new Vector3(0.5f, 0.25f, 0.75f), HeightCollider = 1.25f,
        HeightPivot = 0.25f, UseCapsule = capsule
    };
}
