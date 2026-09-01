using AAEmu.Commons.Network;

namespace AAEmu.UnitTests.Commons.Network;

public class ConnectionEventLimiterTests
{
    [Test]
    public async Task TryConsume_ConcurrentCalls_AllowsOnlyConfiguredLimit()
    {
        var limiter = new ConnectionEventLimiter();
        var allowed = 0;

        Parallel.For(0, 100, _ =>
        {
            if (limiter.TryConsume())
                Interlocked.Increment(ref allowed);
        });

        await Assert.That(allowed).IsEqualTo(ConnectionEventLimiter.DefaultLimit);
    }

    [Test]
    public async Task NewInstance_AfterPreviousLimitIsConsumed_HasFreshBudget()
    {
        var exhausted = new ConnectionEventLimiter();
        for (var i = 0; i < ConnectionEventLimiter.DefaultLimit; i++)
            await Assert.That(exhausted.TryConsume()).IsTrue();

        await Assert.That(exhausted.TryConsume()).IsFalse();

        var fresh = new ConnectionEventLimiter();
        await Assert.That(fresh.TryConsume()).IsTrue();
    }
}
