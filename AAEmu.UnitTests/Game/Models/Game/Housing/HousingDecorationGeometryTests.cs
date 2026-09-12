using System.Numerics;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingDecorationGeometryTests
{
    [Test]
    [Arguments("floor", 1f, true)]
    [Arguments("floor", 0.5f, false)]
    [Arguments("floor", -1f, false)]
    [Arguments("wall", 0.5f, true)]
    [Arguments("wall", -0.5f, true)]
    [Arguments("wall", 0f, true)]
    [Arguments("wall", 0.50001f, false)]
    [Arguments("wall", -0.50001f, false)]
    [Arguments("ceiling", -1f, true)]
    [Arguments("ceiling", -0.5f, false)]
    [Arguments("ceiling", 1f, false)]
    [Arguments("pivot", 1f, true)]
    [Arguments("pivot", 0f, false)]
    [Arguments("mesh", 1f, true)]
    [Arguments("mesh", -1f, false)]
    public async Task IsSupportAllowed_UsesExactNativeNormalThresholds(string flag, float normalZ, bool expected)
    {
        var normal = new Vector3(MathF.Sqrt(1 - normalZ * normalZ), 0, normalZ);
        await Assert.That(HousingDecorationGeometry.IsSupportAllowed(Design(flag), normal)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Check_AllCornersInside_TracesFullGardenHeightAndTenMoreMetres(bool hitBuilding)
    {
        var rays = new List<(Vector3 Origin, Vector3 Vector)>();
        var result = Check(Design("floor"), trace: (origin, vector) =>
        {
            rays.Add((origin, vector));
            return new(hitBuilding ? CryIntersection.Intersects : CryIntersection.Clear, false);
        });
        await Assert.That(result).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(rays.Count).IsEqualTo(8);
        await Assert.That(rays.All(ray => ray.Origin.Z == 10 && ray.Vector == new Vector3(0, 0, -30))).IsTrue();
    }

    [Test]
    [Arguments(false, ErrorMessageType.NoErrorMessage)]
    [Arguments(true, ErrorMessageType.HouseCannotDecorateOutOfHouse)]
    public async Task Check_InteriorRayHit_UsesTerrainFlag(bool terrain, ErrorMessageType expected)
    {
        await Assert.That(Check(Design("floor"), trace: (_, _) => new(CryIntersection.Intersects, terrain)))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task Check_GardenOnlyDecoration_RejectsInteriorAndPermitsTerrain()
    {
        await Assert.That(Check(Design("pivot"))).IsEqualTo(ErrorMessageType.HouseCannotDecorateInHouse);
        await Assert.That(Check(Design("pivot"), trace: (_, _) => new(CryIntersection.Intersects, true)))
            .IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Check_RotatedHouse_UsesOrientedBounds()
    {
        var rotation = Matrix4x4.CreateRotationZ(MathF.PI / 4);
        var house = new CryBox(new Vector3(2, 3, 0), new Vector3(2, 0.25f, 2), rotation);
        var decorationBounds = new CryBounds(new Vector3(-0.1f, -0.1f, 0), new Vector3(0.1f, 0.1f, 0.5f));
        var inside = rotation * Matrix4x4.CreateTranslation(house.Center);
        await Assert.That(Check(Design("floor"), decorationBounds, inside, house))
            .IsEqualTo(ErrorMessageType.NoErrorMessage);
        // This point is in the enclosing axis-aligned bounds, but outside the rotated narrow house.
        var outside = Matrix4x4.CreateTranslation(3, 2, 0);
        await Assert.That(Check(Design("floor"), decorationBounds, outside, house))
            .IsEqualTo(ErrorMessageType.HouseCannotDecorateOutOfHouse);
    }

    [Test]
    public async Task Check_OneRotatedCornerOutside_RejectsFloorOnlyDecoration()
    {
        var local = new CryBounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 1));
        var house = new CryBox(Vector3.Zero, new Vector3(1, 1, 2), Matrix4x4.Identity);
        await Assert.That(Check(Design("floor"), local, Matrix4x4.Identity, house))
            .IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(Design("floor"), local, Matrix4x4.CreateRotationZ(MathF.PI / 4), house))
            .IsEqualTo(ErrorMessageType.HouseCannotDecorateOutOfHouse);
    }

    [Test]
    [Arguments(-10f, ErrorMessageType.NoErrorMessage)]
    [Arguments(10f, ErrorMessageType.HouseCannotDecorateOutOfGarden)]
    [Arguments(9.999f, ErrorMessageType.NoErrorMessage)]
    public async Task Check_GardenPivot_UsesInclusiveMinimumAndExclusiveMaximum(float x, ErrorMessageType expected)
    {
        await Assert.That(Check(Design("pivot"), transform: Matrix4x4.CreateTranslation(x, 0, 0)))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task Check_MeshFlag_RequiresAllCornersInsideGarden()
    {
        var bounds = new CryBounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 1));
        var transform = Matrix4x4.CreateTranslation(9, 0, 0);
        await Assert.That(Check(Design("pivot"), bounds, transform)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check(Design("mesh"), bounds, transform)).IsEqualTo(ErrorMessageType.HouseCannotDecorateOutOfGarden);
    }

    [Test]
    public async Task Check_OutsideHouse_WallFlagPermitsWallSupport()
    {
        await Assert.That(Check(Design("wall"), transform: Matrix4x4.CreateTranslation(8, 0, 0), normal: Vector3.UnitX))
            .IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Check_SupportOrRayUnknown_DeniesBeforeCollision(bool unknownRay)
    {
        var queries = 0;
        var result = Check(Design("floor"), supportAllowed: unknownRay,
            trace: (_, _) => new(CryIntersection.Indeterminate, false), overlaps: _ =>
            {
                queries++;
                return CryIntersection.Clear;
            });
        await Assert.That(result).IsEqualTo(ErrorMessageType.HouseCannotDecorateSurface);
        await Assert.That(queries).IsEqualTo(0);
    }

    [Test]
    [Arguments(CryIntersection.Intersects)]
    [Arguments(CryIntersection.Indeterminate)]
    public async Task Check_CollisionOrUnknownGeometry_DeniesPlacement(CryIntersection intersection)
    {
        await Assert.That(Check(Design("floor"), overlaps: _ => intersection))
            .IsEqualTo(ErrorMessageType.HouseCannotDecorateOverlap);
    }

    [Test]
    [Arguments(-1f, 3f, 1.525f, 1.475f)]
    [Arguments(1f, 3f, 2.025f, 0.975f)]
    public async Task TryCreateCollisionBox_ClipsBelowPivotThenInsetsSupport(float minZ, float maxZ,
        float expectedCenterZ, float expectedHalfZ)
    {
        var result = HousingDecorationGeometry.TryCreateCollisionBox(
            new CryBounds(new Vector3(-2, -1, minZ), new Vector3(2, 1, maxZ)), Matrix4x4.Identity, out var box);
        await Assert.That(result).IsTrue();
        await Assert.That(MathF.Abs(box.Center.Z - expectedCenterZ)).IsLessThan(0.00001f);
        await Assert.That(MathF.Abs(box.HalfSize.Z - expectedHalfZ)).IsLessThan(0.00001f);
        await Assert.That(MathF.Abs(box.Center.Z + box.HalfSize.Z - maxZ)).IsLessThan(0.00001f);
    }

    [Test]
    public async Task TryCreateCollisionBox_WallOrientation_RaisesTheBottomAlongLocalZ()
    {
        var rotation = Matrix4x4.CreateRotationY(MathF.PI / 2);
        var transform = rotation * Matrix4x4.CreateTranslation(10, 20, 30);
        var result = HousingDecorationGeometry.TryCreateCollisionBox(
            new CryBounds(new Vector3(-1, -1, -1), new Vector3(1, 1, 3)), transform, out var box);
        await Assert.That(result).IsTrue();
        await Assert.That(Vector3.Distance(box.Center, new Vector3(11.525f, 20, 30))).IsLessThan(0.00001f);
        await Assert.That(box.Orientation).IsEqualTo(rotation);
        await Assert.That(box.HalfSize).IsEqualTo(new Vector3(1, 1, 1.475f));
    }

    [Test]
    [Arguments(0.05f)]
    [Arguments(0f)]
    [Arguments(-1f)]
    public async Task Check_BoxWithoutPositiveCollisionHeight_DoesNotQueryOverlap(float maxZ)
    {
        var queries = 0;
        var result = Check(Design("floor"), new CryBounds(new Vector3(-1, -1, -2), new Vector3(1, 1, maxZ)),
            overlaps: _ => { queries++; return CryIntersection.Intersects; });
        await Assert.That(result).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(queries).IsEqualTo(0);
    }

    [Test]
    [Arguments(0f, 0f, 0f, true)]
    [Arguments(10f, 10f, 10f, true)]
    [Arguments(12f, 0f, 0f, true)]
    [Arguments(-12f, 0f, 0f, true)]
    [Arguments(0f, 0f, 12f, true)]
    [Arguments(12.001f, 0f, 0f, false)]
    [Arguments(0f, 0f, -12.001f, false)]
    [Arguments(11f, 11f, 11f, true)]
    [Arguments(12f, 12f, 0f, false)]
    public async Task IsWithinSelectionRange_UsesTwoMetreDistanceToGardenVolume(float x, float y, float z, bool expected)
    {
        var garden = new CryBounds(new Vector3(-10), new Vector3(10));
        await Assert.That(HousingDecorationGeometry.IsWithinSelectionRange(garden, new Vector3(x, y, z)))
            .IsEqualTo(expected);
    }

    private static HousingDecoration Design(string flag) => new()
    {
        AllowOnFloor = flag == "floor", AllowOnWall = flag == "wall", AllowOnCeiling = flag == "ceiling",
        AllowPivotOnGarden = flag == "pivot", AllowMeshOnGarden = flag == "mesh"
    };

    private static ErrorMessageType Check(HousingDecoration design, CryBounds? bounds = null, Matrix4x4? transform = null,
        CryBox house = null, Vector3? normal = null, bool supportAllowed = true,
        Func<Vector3, Vector3, HousingDecorationRayResult> trace = null, Func<CryBox, CryIntersection> overlaps = null) =>
        HousingDecorationGeometry.Check(design,
            bounds ?? new CryBounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 1)),
            transform ?? Matrix4x4.Identity, house ?? new CryBox(Vector3.Zero, new Vector3(5), Matrix4x4.Identity),
            new CryBounds(new Vector3(-10), new Vector3(10)), true, normal ?? Vector3.UnitZ, supportAllowed,
            trace ?? ((_, _) => new(CryIntersection.Clear, false)), overlaps ?? (_ => CryIntersection.Clear));
}
