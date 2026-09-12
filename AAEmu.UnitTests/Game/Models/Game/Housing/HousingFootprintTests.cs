using System.Numerics;

using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingFootprintTests
{
    [Test]
    [Arguments(4f, 8f)]
    [Arguments(7.5f, 16f)]
    [Arguments(8f, 16f)]
    [Arguments(9.5f, 20f)]
    [Arguments(10f, 20f)]
    [Arguments(11f, 24f)]
    [Arguments(13f, 28f)]
    [Arguments(14f, 28f)]
    [Arguments(16.5f, 36f)]
    [Arguments(20f, 40f)]
    [Arguments(22f, 44f)]
    public async Task TryCreateGarden_CompactRadii_UseNativeCellSizes(float radius, float expectedSize)
    {
        await Assert.That(HousingFootprint.TryCreateGarden(new Vector2(1024, 2048), radius, 0,
            out var footprint)).IsTrue();
        await Assert.That(footprint.MaxX - footprint.MinX).IsEqualTo(expectedSize);
        await Assert.That(footprint.MaxY - footprint.MinY).IsEqualTo(expectedSize);
    }

    [Test]
    public async Task TryCreateGarden_OffGridPosition_UsesSnappedBounds()
    {
        await Assert.That(HousingFootprint.TryCreateGarden(new Vector2(101.9f, 202.1f), 7.5f, 0,
            out var footprint)).IsTrue();
        await Assert.That(footprint).IsEqualTo(new HousingFootprint(92, 196, 108, 212));
    }

    [Test]
    public async Task TryCreateGarden_NegativePosition_TruncatesTowardZero()
    {
        await Assert.That(HousingFootprint.TryCreateGarden(new Vector2(-101.9f, -202.1f), 7.5f, 0,
            out var footprint)).IsTrue();
        await Assert.That(footprint).IsEqualTo(new HousingFootprint(-104, -208, -88, -192));
    }

    [Test]
    public async Task Contains_AlleyAndHalfOpenEdges_MatchGardenPermission()
    {
        HousingFootprint.TryCreateGarden(new Vector2(100, 200), 7.5f, 0.5f, out var footprint);
        await Assert.That(footprint).IsEqualTo(new HousingFootprint(92.5f, 192.5f, 107.5f, 207.5f));
        await Assert.That(footprint.Contains(92.5f, 192.5f)).IsTrue();
        await Assert.That(footprint.Contains(92.49f, 200)).IsFalse();
        await Assert.That(footprint.Contains(100, 192.49f)).IsFalse();
        await Assert.That(footprint.Contains(107.5f, 200)).IsFalse();
        await Assert.That(footprint.Contains(100, 207.5f)).IsFalse();
        await Assert.That(footprint.Contains(107.49f, 207.49f)).IsTrue();
        await Assert.That(footprint.Contains(float.NaN, 200)).IsFalse();
        await Assert.That(footprint.Contains(100, float.PositiveInfinity)).IsFalse();
    }

    [Test]
    [Arguments(0f, 0f)]
    [Arguments(-1f, 0f)]
    [Arguments(22.5f, 0f)]
    [Arguments(float.NaN, 0f)]
    [Arguments(float.PositiveInfinity, 0f)]
    [Arguments(7.5f, -1f)]
    [Arguments(7.5f, float.NaN)]
    [Arguments(7.5f, 8f)]
    public async Task TryCreateGarden_InvalidGarden_DoesNotInventBounds(float radius, float alley)
    {
        await Assert.That(HousingFootprint.TryCreateGarden(Vector2.Zero, radius, alley, out _)).IsFalse();
    }

    [Test]
    public async Task TryCreateGarden_InvalidCoordinates_DoesNotCreateFootprint()
    {
        await Assert.That(HousingFootprint.TryCreateGarden(new Vector2(float.NaN, 1), 7.5f, 0, out _)).IsFalse();
        await Assert.That(HousingFootprint.TryCreateGarden(new Vector2(1, float.PositiveInfinity), 7.5f, 0, out _)).IsFalse();
        await Assert.That(HousingFootprint.TryCreateGarden(new Vector2(float.MaxValue, 1), 7.5f, 0, out _)).IsFalse();
    }
}
