using AAEmu.Game.Models.Game.Skills.Plots.Tree;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public class PlotTreeTests
{
    [Test]
    public async Task ShouldStartCooldown_WhenPlotCompletes_ReturnsTrue()
    {
        var result = PlotTree.ShouldStartCooldown(cancellationRequested: false, isCasting: false);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldStartCooldown_WhenCastIsCancelledBeforeFiring_ReturnsFalse()
    {
        var result = PlotTree.ShouldStartCooldown(cancellationRequested: true, isCasting: true);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldStartCooldown_WhenAlreadyFiredPlotIsCancelled_ReturnsTrue()
    {
        var result = PlotTree.ShouldStartCooldown(cancellationRequested: true, isCasting: false);

        await Assert.That(result).IsTrue();
    }
}
