using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryGeometrySceneTests
{
    [Test]
    public async Task Raycast_ReturnsNearestAuthoredSurfaceAndSupportIdentity()
    {
        var scene = new CryGeometryScene(_ => [Box(20, 0), Box(21, 5)], _ => true);
        var result = scene.Raycast(new Vector3(0, 0, 10), -Vector3.UnitZ, 20, out var hit);
        await Assert.That(result).IsEqualTo(CryIntersection.Intersects);
        await Assert.That(hit.ObjectId).IsEqualTo(21u);
        await Assert.That(hit.Hit.Distance).IsEqualTo(4f);
        await Assert.That(hit.Hit.Normal).IsEqualTo(Vector3.UnitZ);
    }

    [Test]
    public async Task IntersectBox_UsesProxyInsteadOfLargerRenderBounds()
    {
        var scene = new CryGeometryScene(_ => [Box(20, 0)], _ => true);
        var box = new CryBox(new Vector3(4, 0, 0), Vector3.One, Matrix4x4.Identity);
        await Assert.That(scene.IntersectBox(box)).IsEqualTo(CryIntersection.Clear);
        await Assert.That(scene.IntersectBox(box with { Center = new Vector3(1, 0, 0) }))
            .IsEqualTo(CryIntersection.Intersects);
    }

    [Test]
    public async Task Query_UnresolvedPhysicalPoseDoesNotApprovePlacement()
    {
        var source = Box(20, 0);
        var posed = source with { Asset = source.Asset with
        {
            HasAnimatedCollision = true,
            PoseRequirements = [new CryGeometryPoseRequirement("door.cga", "door", Matrix4x4.Identity, "open", true, true)]
        } };
        var scene = new CryGeometryScene(_ => [posed], _ => true);
        await Assert.That(scene.Raycast(new Vector3(0, 0, 10), -Vector3.UnitZ, 20, out _))
            .IsEqualTo(CryIntersection.Indeterminate);
        await Assert.That(scene.IntersectBox(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity)))
            .IsEqualTo(CryIntersection.Indeterminate);
    }

    [Test]
    public async Task Query_FiltersNoncollidingPartsBeforeNarrowPhase()
    {
        var scene = new CryGeometryScene(_ => [Box(20, 0)], _ => false);
        await Assert.That(scene.Raycast(new Vector3(0, 0, 10), -Vector3.UnitZ, 20, out _))
            .IsEqualTo(CryIntersection.Clear);
        await Assert.That(scene.IntersectBox(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity)))
            .IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task RayOnlyDoodad_OverridesAllProxyFlagsAndDoesNotBlockPlacement()
    {
        var source = Box(20, 0);
        var part = source.Asset.Parts[0] with { PhysicsType = CryGeometryLayerRules.Obstruct, PickingIndex = 3 };
        var instance = source with { RayOnly = true, Asset = source.Asset with { Parts = [part] } };
        var scene = new CryGeometryScene(_ => [instance], _ => true);
        await Assert.That(scene.Raycast(new Vector3(0, 0, 10), -Vector3.UnitZ, 20, out var hit))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(hit.PickingIndex).IsEqualTo(3);
        await Assert.That(scene.IntersectBox(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity)))
            .IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task LivingActor_OnlyParticipatesInOverlapMask()
    {
        var instance = Box(20, 0) with { EntityType = 8 };
        var scene = new CryGeometryScene(_ => [instance], _ => true);
        await Assert.That(scene.Raycast(new Vector3(0, 0, 10), -Vector3.UnitZ, 20, out _))
            .IsEqualTo(CryIntersection.Clear);
        await Assert.That(scene.IntersectBox(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity)))
            .IsEqualTo(CryIntersection.Intersects);
    }

    [Test]
    public async Task Vegetation_RaysUseSolidGeometryInsteadOfTheExtraFoliageProxy()
    {
        var source = Box(20, 0);
        var solid = source.Asset.Parts[0];
        var foliage = solid with { PhysicsType = 0x1001, Transform = Matrix4x4.CreateTranslation(0, 0, 5) };
        var vegetation = source with { IsVegetation = true, Asset = source.Asset with { Parts = [solid, foliage] } };
        var scene = new CryGeometryScene(_ => [vegetation], _ => true);
        await Assert.That(scene.Raycast(new Vector3(0, 0, 10), -Vector3.UnitZ, 20, out var hit))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(hit.Hit.Distance).IsEqualTo(9f);
        await Assert.That(scene.IntersectBox(new CryBox(new Vector3(0, 0, 5), Vector3.One, Matrix4x4.Identity)))
            .IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task PassivePhysicalChildBesideAnimatedVisual_UsesTheResolvedCollider()
    {
        var source = Box(20, 0);
        var instance = source with { Asset = source.Asset with
        {
            HasAnimatedCollision = true,
            PoseRequirements = [new CryGeometryPoseRequirement("flag.cga", "flag", Matrix4x4.Identity,
                "Default", true, true) { AffectsCollision = false }]
        } };
        var scene = new CryGeometryScene(_ => [instance], _ => true);
        await Assert.That(scene.Raycast(new Vector3(0, 0, 10), -Vector3.UnitZ, 20, out _))
            .IsEqualTo(CryIntersection.Intersects);
        await Assert.That(scene.IntersectBox(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity)))
            .IsEqualTo(CryIntersection.Intersects);
    }

    private static CryGeometryInstance Box(uint id, float height) => new(id,
        new CryGeometryAsset(new CryBounds(new Vector3(-10), new Vector3(10)),
            [new CryGeometryPart(new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity),
                Matrix4x4.Identity, 0x1000, "", "test")]), Matrix4x4.CreateTranslation(0, 0, height));
}
