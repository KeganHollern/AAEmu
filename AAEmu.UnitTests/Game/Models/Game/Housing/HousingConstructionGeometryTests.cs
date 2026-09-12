using System.Numerics;
using AAEmu.Game.Models.CryEngine.Physics;

using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingConstructionGeometryTests
{
    [Test]
    public async Task OverlapsHouse_TwoPlotsIgnoreVerticalSeparation()
    {
        var template = new HousingTemplate { GardenRadius = 8, Alley = 1 };
        var bounds = new CryBounds(new Vector3(-2), new Vector3(2));
        await Assert.That(HousingConstructionGeometry.OverlapsHouse(template, bounds,
            Matrix4x4.CreateTranslation(100, 100, 0), template, bounds,
            Matrix4x4.CreateTranslation(100, 100, 1000))).IsTrue();
        await Assert.That(HousingConstructionGeometry.OverlapsHouse(template, bounds,
            Matrix4x4.CreateTranslation(100, 100, 0), template, bounds,
            Matrix4x4.CreateTranslation(116, 100, 0))).IsFalse();
    }

    [Test]
    public async Task OverlapsHouse_ZeroRadiusUsesOrientedModelAndVerticalSeparation()
    {
        var template = new HousingTemplate();
        var bounds = new CryBounds(new Vector3(-2, -0.1f, -1), new Vector3(2, 0.1f, 1));
        var diagonal = Matrix4x4.CreateRotationZ(MathF.PI / 4);
        await Assert.That(HousingConstructionGeometry.OverlapsHouse(template, bounds, diagonal,
            template, bounds, diagonal * Matrix4x4.CreateTranslation(0, 0, 3))).IsFalse();
        await Assert.That(HousingConstructionGeometry.OverlapsHouse(template, bounds, diagonal,
            template, bounds, diagonal * Matrix4x4.CreateTranslation(0, 0, 2))).IsTrue();
    }

    [Test]
    [Arguments(1u, 99f, ErrorMessageType.HouseLandOnly)]
    [Arguments(1u, 100f, ErrorMessageType.NoErrorMessage)]
    [Arguments(7u, 99f, ErrorMessageType.NoErrorMessage)]
    [Arguments(7u, 100f, ErrorMessageType.HouseUnderWaterOnly)]
    [Arguments(15u, 99f, ErrorMessageType.NoErrorMessage)]
    [Arguments(15u, 101f, ErrorMessageType.HouseUnderWaterOnly)]
    public async Task CheckWater_UsesNativeCategoriesAndStrictSurfaceTest(uint category, float height, ErrorMessageType expected)
    {
        await Assert.That(HousingConstructionGeometry.CheckWater(category, height, 100f)).IsEqualTo(expected);
    }

    [Test]
    public async Task Range_UsesTruncatedThreeDimensionalDistanceAndGardenRadius()
    {
        await Assert.That(HousingConstructionGeometry.IsWithinRange(Vector3.Zero, new(38.99f, 0, 0), 8)).IsTrue();
        await Assert.That(HousingConstructionGeometry.IsWithinRange(Vector3.Zero, new(39, 0, 0), 8)).IsFalse();
        await Assert.That(HousingConstructionGeometry.IsWithinRange(Vector3.Zero, new(0, 0, 39), 8)).IsFalse();
    }

    [Test]
    public async Task PlotSnap_UsesFourMeterGridWithFootprintCenterOffset()
    {
        await Assert.That(HousingConstructionGeometry.SnapPlot(new(11.9f, 15.9f), 4)).IsEqualTo(new Vector2(12, 16));
        await Assert.That(HousingConstructionGeometry.SnapPlot(new(9.9f, 13.9f), 2)).IsEqualTo(new Vector2(10, 14));
    }

    [Test]
    public async Task ManualZ_ReplacesClaimedHeightWithMinimumOfCornersAndCenter()
    {
        var pose = Resolve(false, new(-2, -1, -5), new(2, 1, 5),
            (x, y) => x == 20 && y == 20 ? 7 : 10);
        await Assert.That(pose.IsValid).IsTrue();
        await Assert.That(pose.Position.Z).IsEqualTo(7f);
    }

    [Test]
    public async Task ManualZ_SixMeterSpreadIsAllowedButLargerSpreadFails()
    {
        var atLimit = Resolve(false, new(-2, -1, -5), new(2, 1, 5), (x, _) => x < 20 ? 10 : 16);
        var overLimit = Resolve(false, new(-2, -1, -5), new(2, 1, 5), (x, _) => x < 20 ? 10 : 16.01f);
        await Assert.That(atLimit.IsValid).IsTrue();
        await Assert.That(overLimit.Error).IsEqualTo(ErrorMessageType.HouseCannotLocateTerrainTooLow);
    }

    [Test]
    public async Task Rotation_ChangesTerrainSamplesAndKeepsOrdinaryHouseYaw()
    {
        var unrotated = Resolve(false, new(-4, -1, -5), new(4, 1, 5), (x, _) => x - 10);
        var rotated = Resolve(false, new(-4, -1, -5), new(4, 1, 5), (x, _) => x - 10, MathF.PI / 2);
        await Assert.That(unrotated.Error).IsEqualTo(ErrorMessageType.HouseCannotLocateTerrainTooLow);
        await Assert.That(rotated.IsValid).IsTrue();
        await Assert.That(rotated.Yaw).IsEqualTo(MathF.PI / 2);
        await Assert.That(rotated.Position.Z).IsEqualTo(9f);
    }

    [Test]
    public async Task AutoZ_InteriorGridPeakSetsHeightAndSupportUsesModelBottom()
    {
        static float Terrain(float x, float y) => x == 20 && y == 20 ? 16 : 10;
        var supported = Resolve(true, new(-4, -2, -6), new(4, 2, 10), Terrain);
        var unsupported = Resolve(true, new(-4, -2, -5), new(4, 2, 10), Terrain);
        await Assert.That(supported.IsValid).IsTrue();
        await Assert.That(supported.Position.Z).IsEqualTo(16f);
        await Assert.That(unsupported.Error).IsEqualTo(ErrorMessageType.HouseCannotLocateTerrainTooLow);
    }

    [Test]
    public async Task AutoZ_PerimeterMidpointContributesAndGridOutsideBoundsDoesNot()
    {
        static float Terrain(float x, float y) => y == 22 ? float.NaN : x == 20 && y == 21 ? 12 : 10;
        var pose = Resolve(true, new(-4, -1, -3), new(4, 1, 5), Terrain);
        await Assert.That(pose.IsValid).IsTrue();
        await Assert.That(pose.Position.Z).IsEqualTo(12f);
    }

    [Test]
    public async Task Stronghold_UsesExplicitGridPhaseAndQuantizedHeightAndRotation()
    {
        var pose = HousingConstructionGeometry.Resolve(new HousingTemplate { CategoryId = 5 },
            new(25, 26, 3000), 0.8f, new(30, 30, 6), -Vector3.One, Vector3.One, (_, _) => 4, 2, new(10, 10));
        await Assert.That(pose.IsValid).IsTrue();
        await Assert.That(pose.Position).IsEqualTo(new Vector3(30, 30, 6));
        await Assert.That(pose.Yaw).IsEqualTo(MathF.PI / 2);
        var absentPhase = HousingConstructionGeometry.Resolve(new HousingTemplate { CategoryId = 5 },
            new(25, 26, 3000), 0.8f, new(30, 30, 6), -Vector3.One, Vector3.One, (_, _) => 4, 2);
        await Assert.That(absentPhase.IsValid).IsFalse();
    }

    [Test]
    public async Task OrdinaryPlot_SnapsBeforeTerrainAndRangeCheck()
    {
        var pose = HousingConstructionGeometry.Resolve(new HousingTemplate { CategoryId = 1, GardenRadius = 4 },
            new(11.9f, 15.9f, 999), 0.23f, new(12, 16, 10), -Vector3.One, Vector3.One, (_, _) => 10, 2);
        await Assert.That(pose.IsValid).IsTrue();
        await Assert.That(pose.Position).IsEqualTo(new Vector3(12, 16, 10));
        await Assert.That(pose.Yaw).IsEqualTo(0.23f);
    }

    [Test]
    public async Task MissingTerrainOrNonfinitePose_Fails()
    {
        var missing = Resolve(false, -Vector3.One, Vector3.One, (_, _) => float.NaN);
        await Assert.That(missing.Error).IsEqualTo(ErrorMessageType.HouseCannotLocateInvalidArea);
        var nonfinite = Resolve(false, -Vector3.One, Vector3.One, (_, _) => 10, float.NaN);
        await Assert.That(nonfinite.Error).IsEqualTo(ErrorMessageType.HouseCannotLocateInvalidArea);
    }

    private static HousingConstructionPose Resolve(bool autoZ, Vector3 min, Vector3 max,
        Func<float, float, float> terrain, float yaw = 0) =>
        HousingConstructionGeometry.Resolve(new HousingTemplate { CategoryId = 1, AutoZ = autoZ },
            new(20, 20, 15), yaw, new(20, 20, 10), min, max, terrain, 2);
}
