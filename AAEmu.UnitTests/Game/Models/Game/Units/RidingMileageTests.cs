using System.Numerics;

using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed class RidingMileageTests
{
    private static readonly DateTime Start = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task FirstObservation_SetsBaselineWithoutCountingTheIncomingSegment()
    {
        var mileage = new RidingMileage();

        await Assert.That(mileage.Observe(Vector3.Zero, new Vector3(10, 0, 0), Start, true)).IsEqualTo(0);
        await Assert.That(mileage.Observe(new Vector3(10, 0, 0), new Vector3(12, 0, 0),
            Start.AddSeconds(1), true)).IsEqualTo(2);
    }

    [Test]
    public async Task SubMetreSegments_AccumulateWholeMetresWithoutDiscardingTheRemainder()
    {
        var mileage = Begin();

        await Assert.That(Observe(mileage, 0, 0.25f, 1)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 0.25f, 0.75f, 2)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 0.75f, 1.5f, 3)).IsEqualTo(1);
        await Assert.That(Observe(mileage, 1.5f, 2, 4)).IsEqualTo(1);
    }

    [Test]
    public async Task DiagonalMovement_CountsHorizontalPathLength()
    {
        var mileage = Begin();

        await Assert.That(mileage.Observe(Vector3.Zero, new Vector3(3, 4, 12),
            Start.AddSeconds(1), true)).IsEqualTo(5);
    }

    [Test]
    public async Task StationaryAndVerticalOnlyMovement_DoNotAddDistance()
    {
        var mileage = Begin();

        await Assert.That(mileage.Observe(Vector3.Zero, Vector3.Zero, Start.AddSeconds(1), true)).IsEqualTo(0);
        await Assert.That(mileage.Observe(Vector3.Zero, new Vector3(0, 0, 20),
            Start.AddSeconds(2), true)).IsEqualTo(0);
        await Assert.That(mileage.Observe(new Vector3(0, 0, 20), new Vector3(1, 0, 20),
            Start.AddSeconds(3), true)).IsEqualTo(1);
    }

    [Test]
    public async Task Stop_PreservesFractionalDistanceForTheNextMovement()
    {
        var mileage = Begin();
        await Assert.That(Observe(mileage, 0, 0.75f, 1)).IsEqualTo(0);

        await Assert.That(Observe(mileage, 0.75f, 0.75f, 10)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 0.75f, 1, 11)).IsEqualTo(1);
    }

    [Test]
    public async Task ResetMovement_DropsTheOldBaselineAndPreservesFractionalDistance()
    {
        var mileage = Begin();
        await Assert.That(Observe(mileage, 0, 0.75f, 1)).IsEqualTo(0);

        mileage.ResetMovement();

        await Assert.That(Observe(mileage, 100, 100.5f, 2)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 100.5f, 100.75f, 3)).IsEqualTo(1);
    }

    [Test]
    public async Task IneligibleMovement_ClearsTheBaselineWithoutCountingTheReturnToRiding()
    {
        var mileage = Begin();
        await Assert.That(Observe(mileage, 0, 0.75f, 1)).IsEqualTo(0);

        await Assert.That(mileage.Observe(new Vector3(0.75f, 0, 0), new Vector3(100, 0, 0),
            Start.AddSeconds(2), false)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 100, 100.5f, 3)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 100.5f, 100.75f, 4)).IsEqualTo(1);
    }

    [Test]
    public async Task DiscontinuousPreviousPosition_DoesNotBridgeAnUnobservedMove()
    {
        var mileage = Begin();
        await Assert.That(Observe(mileage, 0, 0.5f, 1)).IsEqualTo(0);

        await Assert.That(Observe(mileage, 100, 110, 2)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 110, 110.5f, 3)).IsEqualTo(1);
    }

    [Test]
    [Arguments(100f, 4d, 100)]
    [Arguments(100.25f, 4d, 0)]
    [Arguments(33f, 1d, 33)]
    [Arguments(33.25f, 1d, 0)]
    public async Task MovementBounds_RespectTheDistanceLimitAndInitialBurstAllowance(
        float distance, double seconds, int expectedMileage)
    {
        var mileage = Begin();

        await Assert.That(Observe(mileage, 0, distance, seconds)).IsEqualTo(expectedMileage);
    }

    [Test]
    [Arguments(101f, 4d)]
    [Arguments(34f, 1d)]
    public async Task AnomalousSegment_AddsNoDistanceAndAllowsLaterValidMovement(float distance, double seconds)
    {
        var mileage = Begin();

        await Assert.That(Observe(mileage, 0, distance, seconds)).IsEqualTo(0);
        await Assert.That(Observe(mileage, distance, distance + 1, seconds + 1)).IsEqualTo(1);
    }

    [Test]
    [Arguments(float.NaN, 0f, 0f, true)]
    [Arguments(0f, float.PositiveInfinity, 0f, true)]
    [Arguments(0f, 0f, float.NegativeInfinity, true)]
    [Arguments(float.PositiveInfinity, 0f, 0f, false)]
    [Arguments(0f, float.NaN, 0f, false)]
    [Arguments(0f, 0f, float.NegativeInfinity, false)]
    public async Task NonfinitePosition_ClearsTheBaselineAndPreservesEarlierFractionalDistance(
        float x, float y, float z, bool invalidPreviousPosition)
    {
        var mileage = Begin();
        await Assert.That(Observe(mileage, 0, 0.75f, 1)).IsEqualTo(0);
        var invalid = new Vector3(x, y, z);
        var previous = invalidPreviousPosition ? invalid : new Vector3(0.75f, 0, 0);
        var current = invalidPreviousPosition ? new Vector3(1, 0, 0) : invalid;

        await Assert.That(mileage.Observe(previous, current, Start.AddSeconds(2), true)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 100, 100.5f, 3)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 100.5f, 100.75f, 4)).IsEqualTo(1);
    }

    [Test]
    [Arguments(0d)]
    [Arguments(-1d)]
    public async Task NonIncreasingTimestamp_DoesNotAddMileageAndLaterMovementCanResume(double seconds)
    {
        var mileage = Begin();

        await Assert.That(Observe(mileage, 0, 10, seconds)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 10, 11, 1)).IsEqualTo(1);
    }

    [Test]
    public async Task EqualRouteWithDifferentNormalPacketCounts_AddsTheSameDistance()
    {
        var wholeSegment = Begin();
        var splitSegments = Begin();
        var wholeMileage = Observe(wholeSegment, 0, 10, 1);
        var splitMileage = 0;
        for (var segment = 1; segment <= 40; segment++)
            splitMileage += Observe(splitSegments, (segment - 1) * 0.25f, segment * 0.25f, segment * 0.025);

        await Assert.That(wholeMileage).IsEqualTo(10);
        await Assert.That(splitMileage).IsEqualTo(wholeMileage);
    }

    [Test]
    public async Task RapidPackets_ShareOneDistanceAllowanceInsteadOfGettingABurstEach()
    {
        var mileage = Begin();
        var earnedMileage = 0;

        for (var segment = 1; segment <= 100; segment++)
            earnedMileage += Observe(mileage, segment - 1, segment, segment * 0.001);

        // The 3-metre initial burst plus 30 metres/second permits at most 6 metres in 100 ms.
        await Assert.That(earnedMileage).IsGreaterThanOrEqualTo(3);
        await Assert.That(earnedMileage).IsLessThanOrEqualTo(6);
    }

    [Test]
    public async Task StationarySamples_RefillTheAllowanceForALaterValidLagBurst()
    {
        var mileage = Begin();

        await Assert.That(Observe(mileage, 0, 0, 4)).IsEqualTo(0);
        await Assert.That(Observe(mileage, 0, 100, 4.01)).IsEqualTo(100);
    }

    private static RidingMileage Begin()
    {
        var mileage = new RidingMileage();
        mileage.Observe(Vector3.Zero, Vector3.Zero, Start, true);
        return mileage;
    }

    private static int Observe(RidingMileage mileage, float previousX, float currentX, double seconds) =>
        mileage.Observe(new Vector3(previousX, 0, 0), new Vector3(currentX, 0, 0), Start.AddSeconds(seconds), true);
}
