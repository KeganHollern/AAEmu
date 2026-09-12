using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.CryEngine;

public sealed class CryGeometryLayerRulesTests
{
    [Test]
    public async Task SingleLayers_SelectDefaultSolidRayAndObstructRoles()
    {
        await Assert.That(Usage(0x1000, [0x1000])).IsEqualTo(CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap);
        await Assert.That(Usage(0x1001, [0x1001])).IsEqualTo(CryGeometryQueryUsage.Ray);
        await Assert.That(Usage(0x1002, [0x1002])).IsEqualTo(CryGeometryQueryUsage.None);
        await Assert.That(Usage(0x1003, [0x1003])).IsEqualTo(CryGeometryQueryUsage.None);
    }

    [Test]
    public async Task SolidAndOneOtherLayer_UseSeparateRayAndCollisionGeometry()
    {
        foreach (var other in new[] { 0x1001, 0x1002, 0x1003 })
        {
            await Assert.That(Usage(0x1000, [0x1000, other])).IsEqualTo(CryGeometryQueryUsage.PlacementOverlap);
            await Assert.That(Usage(other, [0x1000, other])).IsEqualTo(CryGeometryQueryUsage.Ray);
        }
    }

    [Test]
    public async Task ThreeLayers_PreserveSolidRaysAndIgnoreObstructForHousing()
    {
        int[] layers = [0x1000, 0x1001, 0x1002];
        await Assert.That(Usage(0x1000, layers)).IsEqualTo(CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap);
        await Assert.That(Usage(0x1001, layers)).IsEqualTo(CryGeometryQueryUsage.Ray);
        await Assert.That(Usage(0x1002, layers)).IsEqualTo(CryGeometryQueryUsage.None);
    }

    [Test]
    public async Task FoliageSpines_RemoveExtraProxyLayersFromHousingQueries()
    {
        await Assert.That(Usage(0x1000, [0x1000, 0x1001], 2))
            .IsEqualTo(CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap);
        await Assert.That(Usage(0x1001, [0x1000, 0x1001], 2)).IsEqualTo(CryGeometryQueryUsage.None);
        await Assert.That(Usage(0x1001, [0x1000, 0x1001, 0x1002], 2)).IsEqualTo(CryGeometryQueryUsage.None);
        await Assert.That(Usage(0x1002, [0x1000, 0x1001, 0x1002], 2)).IsEqualTo(CryGeometryQueryUsage.None);
    }

    [Test]
    public async Task DistinctStatobjects_DoNotCombineTheirLayers()
    {
        await Assert.That(Usage(0x1000, [0x1000])).IsEqualTo(CryGeometryQueryUsage.Ray | CryGeometryQueryUsage.PlacementOverlap);
        await Assert.That(Usage(0x1001, [0x1001])).IsEqualTo(CryGeometryQueryUsage.Ray);
        await Assert.That(Usage(0x1001, [0x1000])).IsEqualTo(CryGeometryQueryUsage.None);
    }

    private static CryGeometryQueryUsage Usage(int type, int[] layers, int spines = 0) =>
        CryGeometryLayerRules.GetHousingUsage(type, layers, spines);
}
