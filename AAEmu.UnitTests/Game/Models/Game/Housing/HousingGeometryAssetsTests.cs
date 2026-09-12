using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingGeometryAssetsTests
{
    [Test]
    public async Task GardenBounds_ReservesAlleyAndUsesRotatedModelHeight()
    {
        var template = new HousingTemplate { GardenRadius = 8, Alley = 1, ExtraHeightAbove = 2, ExtraHeightBelow = 3 };
        var local = new CryBounds(new(-6, -4, -1), new(6, 4, 7));
        var transform = Matrix4x4.CreateRotationZ(MathF.PI / 4) * Matrix4x4.CreateTranslation(100, 100, 10);
        var bounds = HousingGeometryAssets.GardenBounds(template, local, transform);
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(93, 93, 6));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(107, 107, 19));
    }

    [Test]
    public async Task GardenBounds_ZeroRadiusUsesAuthoredBounds()
    {
        var template = new HousingTemplate { GardenRadius = 0 };
        var local = new CryBounds(new(-6, -4, -1), new(6, 4, 7));
        var bounds = HousingGeometryAssets.GardenBounds(template, local, Matrix4x4.CreateTranslation(100, 100, 10));
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(94, 96, 9));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(106, 104, 17));
        await Assert.That(HousingDecorationGeometry.IsWithinSelectionRange(bounds, new Vector3(100, 100, 100))).IsFalse();
    }

    [Test]
    public async Task GardenBounds_FullPlotDoesNotReserveAlley()
    {
        var template = new HousingTemplate { GardenRadius = 8, Alley = 1 };
        var bounds = HousingGeometryAssets.GardenBounds(template, new CryBounds(Vector3.Zero, Vector3.One),
            Matrix4x4.CreateTranslation(100, 100, 0), false);
        await Assert.That(bounds.Min.X).IsEqualTo(92f);
        await Assert.That(bounds.Max.X).IsEqualTo(108f);
    }
}
