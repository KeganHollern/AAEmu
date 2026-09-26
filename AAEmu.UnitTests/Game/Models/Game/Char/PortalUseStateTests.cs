using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed class PortalUseStateTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Delay_StartsFromCompletionAndAllowsTheExactBoundary()
    {
        var state = new PortalUseState();
        await Assert.That(state.TryBeginTeleport(_now)).IsTrue();
        await Assert.That(state.TryBeginTeleport(_now.AddMinutes(1))).IsFalse();
        state.CompleteTeleport(_now.AddMinutes(1));
        await Assert.That(state.TryBeginTeleport(_now.AddMinutes(1).AddMilliseconds(9999))).IsFalse();
        await Assert.That(state.TryBeginTeleport(_now.AddMinutes(1).AddSeconds(10))).IsTrue();
    }

    [Test]
    public async Task EarlierCompletionTimestampCannotShortenTheDelay()
    {
        var state = new PortalUseState();
        state.CompleteTeleport(_now);
        state.CompleteTeleport(_now.AddSeconds(-5));
        await Assert.That(state.TryBeginTeleport(_now.AddMilliseconds(9999))).IsFalse();
        await Assert.That(state.TryBeginTeleport(_now.AddSeconds(10))).IsTrue();
    }

    [Test]
    public async Task ConcurrentRequests_OnlyOneCanStart()
    {
        var state = new PortalUseState();
        var started = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            if (state.TryBeginTeleport(_now))
                Interlocked.Increment(ref started);
        })));
        await Assert.That(started).IsEqualTo(1);
    }
}
