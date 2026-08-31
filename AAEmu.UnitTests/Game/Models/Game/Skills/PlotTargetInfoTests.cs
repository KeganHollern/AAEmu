using AAEmu.Game.Models.Game.Skills.Plots.Tree;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public class PlotTargetInfoTests
{
    [Test]
    public async Task ResolvePlotLandHeight_AerialPortalTarget_LandsOnGround()
    {
        var result = PlotTargetInfo.ResolvePlotLandHeight(300f, 0, 223f, previousIsAerial: true);

        await Assert.That(result).IsEqualTo(223f);
    }

    [Test]
    public async Task ResolvePlotLandHeight_LargeVerticalSearchRange_DoesNotLiftImpact()
    {
        var result = PlotTargetInfo.ResolvePlotLandHeight(223f, 500000, 223f, previousIsAerial: false);

        await Assert.That(result).IsEqualTo(223f);
    }

    [Test]
    public async Task ResolvePlotLandHeight_GroundedShallowOffset_PreservesLift()
    {
        var result = PlotTargetInfo.ResolvePlotLandHeight(100f, 3000, 100f, previousIsAerial: false);

        await Assert.That(result).IsEqualTo(103f);
    }

    [Test]
    public async Task ResolvePlotLandHeight_WithoutGroundData_PreservesLegacyHeight()
    {
        var result = PlotTargetInfo.ResolvePlotLandHeight(300f, 500000, null, previousIsAerial: true);

        await Assert.That(result).IsEqualTo(800f);
    }
}
