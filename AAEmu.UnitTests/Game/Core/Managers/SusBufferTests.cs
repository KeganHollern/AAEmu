using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class SusBufferTests
{
    [Test]
    public async Task LogActivity_DoesNotWriteUntilFlushAndPreservesIncidentFields()
    {
        var saved = new List<SusManager.Activity>();
        using var manager = MakeManager(saved);
        manager.LogActivity("Cheat", 1, 2, 3, new Vector3(4, 5, 6), "test");
        await Assert.That(saved.Count).IsEqualTo(0);
        manager.FlushPending();
        await Assert.That(saved.Count).IsEqualTo(1);
        await Assert.That(saved[0].Category).IsEqualTo("Cheat");
        await Assert.That(saved[0].AccountId).IsEqualTo(1u);
        await Assert.That(saved[0].PlayerId).IsEqualTo(2u);
        await Assert.That(saved[0].ZoneGroup).IsEqualTo(3u);
        await Assert.That(saved[0].Position).IsEqualTo(new Vector3(4, 5, 6));
        await Assert.That(saved[0].Description).IsEqualTo("test");
        await Assert.That(manager.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task QueueAndBatch_AreBoundedAndShutdownDrainsAcceptedRecords()
    {
        var batches = new List<int>();
        using var manager = new SusManager(Mock.Of<IWorldManager>().Object)
        {
            WriteBatch = batch => batches.Add(batch.Count)
        };
        for (var index = 0; index < SusManager.QueueCapacity; ++index)
            await Assert.That(manager.LogActivity("Cheat", "test")).IsTrue();
        await Assert.That(manager.LogActivity("Cheat", "excess")).IsFalse();
        await Assert.That(manager.PendingCount).IsEqualTo(SusManager.QueueCapacity);
        manager.FlushPending();
        await Assert.That(batches.Single()).IsEqualTo(SusManager.BatchSize);
        manager.Dispose();
        await Assert.That(batches.Sum()).IsEqualTo(SusManager.QueueCapacity);
        await Assert.That(batches.All(count => count <= SusManager.BatchSize)).IsTrue();
        await Assert.That(manager.LogActivity("Cheat", "after shutdown")).IsFalse();
    }

    [Test]
    public async Task FailedWrite_RetainsAcceptedRowsAndRetriesOncePerFlush()
    {
        var calls = 0;
        using var manager = new SusManager(Mock.Of<IWorldManager>().Object)
        {
            WriteBatch = _ => { ++calls; throw new IOException("database unavailable"); }
        };
        manager.LogActivity("Cheat", "test");
        await Assert.That(manager.FlushPending()).IsFalse();
        await Assert.That(manager.PendingCount).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(1);
        manager.WriteBatch = _ => ++calls;
        await Assert.That(manager.FlushPending()).IsTrue();
        await Assert.That(manager.PendingCount).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Queue_ConcurrentProducersStayBoundedAndTextIsBounded()
    {
        var saved = new List<SusManager.Activity>();
        using var manager = MakeManager(saved);
        Parallel.For(0, SusManager.QueueCapacity * 2, _ => manager.LogActivity(new string('a', 100), new string('b', 10000)));
        await Assert.That(manager.PendingCount).IsEqualTo(SusManager.QueueCapacity);
        manager.Dispose();
        await Assert.That(saved.Count).IsEqualTo(SusManager.QueueCapacity);
        await Assert.That(saved.All(row => row.Category.Length == 64 && row.Description.Length == SusManager.MaximumDescriptionLength)).IsTrue();
    }

    [Test]
    public async Task Queue_RejectsNonfinitePositionsWithoutBlockingValidRecords()
    {
        var saved = new List<SusManager.Activity>();
        using var manager = MakeManager(saved);
        await Assert.That(manager.LogActivity("Cheat", 1, 2, 3, new Vector3(float.NaN, 0, 0), "bad")).IsFalse();
        await Assert.That(manager.LogActivity("Cheat", 1, 2, 3, new Vector3(0, float.PositiveInfinity, 0), "bad")).IsFalse();
        await Assert.That(manager.LogActivity("Cheat", 1, 2, 3, new Vector3(0, 0, float.NegativeInfinity), "bad")).IsFalse();
        await Assert.That(manager.LogActivity("Cheat", 1, 2, 3, Vector3.Zero, "good")).IsTrue();
        manager.FlushPending();
        await Assert.That(saved.Count).IsEqualTo(1);
        await Assert.That(saved[0].Description).IsEqualTo("good");
    }

    private static SusManager MakeManager(List<SusManager.Activity> saved) =>
        new(Mock.Of<IWorldManager>().Object) { WriteBatch = batch => saved.AddRange(batch) };
}
