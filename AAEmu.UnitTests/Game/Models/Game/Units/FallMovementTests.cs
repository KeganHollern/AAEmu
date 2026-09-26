using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed class FallMovementTests
{
    [Test]
    public async Task OmittedLandingReport_TerrainContactCompletesObservedFallOnce()
    {
        var fall = Descend();
        var impact = fall.Observe(0, 1, true, false);
        await Assert.That(impact).IsEqualTo(FallMovement.EncodeImpact(20, 20));
        await Assert.That(fall.Observe(0, 2, true, true)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task MissingTerrain_NativeLandingEventCompletesOnlyAnObservedFall()
    {
        var fall = new FallMovement();
        await Assert.That(fall.Observe(20, 0, false, true)).IsEqualTo((ushort)0);
        await Assert.That(fall.Observe(10, 0.5, false, false)).IsEqualTo((ushort)0);
        await Assert.That(fall.Observe(0, 1, false, true)).IsEqualTo(FallMovement.EncodeImpact(20, 20));
        await Assert.That(fall.Observe(0, 2, false, true)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task AirborneStationaryPacket_DoesNotApplyOrResetDamage()
    {
        var fall = Descend();
        await Assert.That(fall.Observe(10, 0.6, false, false)).IsEqualTo((ushort)0);
        await Assert.That(fall.Observe(10, 0.7, false, false)).IsEqualTo((ushort)0);
        await Assert.That(fall.Observe(0, 1.2, true, false)).IsEqualTo(FallMovement.EncodeImpact(20, 20));
    }

    [Test]
    public async Task JumpApexPause_PreservesOnlyTheLaterDescent()
    {
        var fall = new FallMovement();
        fall.Observe(0, 0, true, false);
        fall.Observe(3, 0.4, false, false);
        fall.Observe(3, 0.5, false, false);
        var impact = fall.Observe(0, 0.6, true, false);
        await Assert.That(impact).IsEqualTo(FallMovement.EncodeImpact(3, 30));
        await Assert.That(impact).IsLessThan((ushort)8600);
    }

    [Test]
    public async Task LedgeLanding_SeparatesTwoFalls()
    {
        var fall = Descend();
        var first = fall.Observe(10, 0.6, false, true);
        fall.Observe(10, 1, false, false);
        var second = fall.Observe(0, 1.5, true, false);
        await Assert.That(first).IsEqualTo(FallMovement.EncodeImpact(10, 20));
        await Assert.That(second).IsEqualTo(FallMovement.EncodeImpact(10, 20));
    }

    [Test]
    public async Task ContinuousGroundedSlope_DoesNotAccumulateFallHeight()
    {
        var fall = new FallMovement();
        for (var i = 0; i <= 100; i++)
            await Assert.That(fall.Observe(100 - i, i * 0.1, true, false)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task StairsWithSmallAirborneSteps_DoNotAccumulateLongFall()
    {
        var fall = new FallMovement();
        fall.Observe(100, 0, true, false);
        for (var i = 1; i <= 100; i++)
        {
            fall.Observe(101 - i, i, false, false);
            await Assert.That(fall.Observe(100 - i, i + 0.1, true, false)).IsLessThan((ushort)8600);
        }
    }

    [Test]
    public async Task FastPacketBurst_IsBoundedByObservedHeight()
    {
        var fall = new FallMovement();
        fall.Observe(1, 0, false, false);
        await Assert.That(fall.Observe(0, 0.00001, true, true)).IsEqualTo(FallMovement.EncodeImpact(1, 100000));
        await Assert.That(FallMovement.EncodeImpact(1, 100000)).IsLessThan((ushort)8600);
    }

    [Test]
    public async Task SlowDescent_IsBoundedByObservedSpeed()
    {
        var fall = new FallMovement();
        fall.Observe(200, 0, false, false);
        await Assert.That(fall.Observe(0, 100, true, true)).IsEqualTo(FallMovement.EncodeImpact(200, 2));
        await Assert.That(FallMovement.EncodeImpact(200, 2)).IsLessThan((ushort)8600);
    }

    [Test]
    public async Task Reset_DiscardsFallAcrossTeleportOrLifecycleChange()
    {
        var fall = Descend();
        fall.Reset();
        await Assert.That(fall.Observe(-100, 1, true, true)).IsEqualTo((ushort)0);
    }

    [Test]
    [Arguments(float.NaN, 20d)]
    [Arguments(float.PositiveInfinity, 20d)]
    [Arguments(20f, double.NaN)]
    [Arguments(20f, double.PositiveInfinity)]
    [Arguments(-1f, 20d)]
    [Arguments(20f, -1d)]
    public async Task InvalidImpact_ReturnsZero(float height, double speed)
    {
        await Assert.That(FallMovement.EncodeImpact(height, speed)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task NativeEncoding_UsesFullUshortRangeAndNativeHeightCap()
    {
        await Assert.That(FallMovement.EncodeImpact(409.6f, 128)).IsEqualTo(ushort.MaxValue);
        await Assert.That(FallMovement.EncodeImpact(100, 100)).IsEqualTo((ushort)32381);
    }

    [Test]
    public async Task SparseGroundedToGroundedLanding_UsesObservedDescentWhenClientReportsContact()
    {
        var fall = new FallMovement();
        fall.Observe(20, 0, true, false);
        await Assert.That(fall.Observe(0, 1, true, true)).IsEqualTo(FallMovement.EncodeImpact(20, 20));
    }

    [Test]
    public async Task SameTimeSamples_DoNotDivideByZeroOrLoseEarlierDescent()
    {
        var fall = new FallMovement();
        fall.Observe(20, 0, false, false);
        fall.Observe(15, 0, false, false);
        fall.Observe(10, 0, false, false);
        await Assert.That(fall.Observe(0, 0.5, true, false)).IsEqualTo(FallMovement.EncodeImpact(20, 20));
    }

    [Test]
    public async Task EntireFallAtSameTimestamp_DoesNotInventSpeed()
    {
        var fall = new FallMovement();
        fall.Observe(200, 0, false, false);
        fall.Observe(100, 0, false, false);
        await Assert.That(fall.Observe(0, 0, true, true)).IsEqualTo((ushort)0);
    }

    private static FallMovement Descend()
    {
        var fall = new FallMovement();
        fall.Observe(20, 0, false, false);
        fall.Observe(10, 0.5, false, false);
        return fall;
    }
}
