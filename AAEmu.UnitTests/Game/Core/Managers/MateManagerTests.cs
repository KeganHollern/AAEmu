using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class MateManagerTests
{
    [Test]
    public async Task TrackingPersistentMate_PrioritizesItAheadOfTemporarySummons()
    {
        var manager = new MateManager(null);
        var temporary = new Mate { IsTemporarySummon = true };
        var persistent = new Mate();

        manager.TrackActiveMate(42, temporary);
        manager.TrackActiveMate(42, persistent, prioritize: true);

        var mates = manager.GetActiveMates(42);
        await Assert.That(mates.Count).IsEqualTo(2);
        await Assert.That(mates[0]).IsSameReferenceAs(persistent);
        await Assert.That(mates[1]).IsSameReferenceAs(temporary);
    }

    [Test]
    public async Task RemovalReservation_RequiresExactTrackedInstanceWhenIdsAreReused()
    {
        var manager = new MateManager(null);
        var expired = new Mate { ObjId = 100, TlId = 10, IsTemporarySummon = true };
        var replacement = new Mate { ObjId = 100, TlId = 10, IsTemporarySummon = true };

        manager.TrackActiveMate(42, expired);
        await Assert.That(manager.TryBeginMateRemoval(42, expired)).IsTrue();
        manager.CompleteMateRemoval(expired);
        manager.TrackActiveMate(42, replacement);

        await Assert.That(manager.TryBeginMateRemoval(42, expired)).IsFalse();
        await Assert.That(manager.TryBeginMateRemoval(42, replacement)).IsTrue();
        manager.CompleteMateRemoval(replacement);
    }

    [Test]
    public async Task BeginningLastMateRemoval_ClearsOwnerRegistryAndSnapshotsAreIsolated()
    {
        var manager = new MateManager(null);
        var mate = new Mate();
        manager.TrackActiveMate(42, mate);

        var snapshot = manager.GetActiveMates(42);
        snapshot.Clear();
        await Assert.That(manager.GetActiveMates(42)).HasSingleItem();

        await Assert.That(manager.TryBeginMateRemoval(42, mate)).IsTrue();
        manager.CompleteMateRemoval(mate);
        await Assert.That(manager.GetActiveMates(42)).IsEmpty();
    }

    [Test]
    public async Task OwnerScopedLookup_DoesNotReturnAnotherOwnersReusedTlId()
    {
        var manager = new MateManager(null);
        var first = new Mate { TlId = 10 };
        var second = new Mate { TlId = 10 };
        manager.TrackActiveMate(42, first);
        manager.TrackActiveMate(84, second);

        await Assert.That(manager.GetActiveMateByTlId(42, 10)).IsSameReferenceAs(first);
        await Assert.That(manager.GetActiveMateByTlId(84, 10)).IsSameReferenceAs(second);
        await Assert.That(manager.GetActiveMateByTlId(21, 10)).IsNull();
    }

    [Test]
    public async Task OwnerDeath_SelectsPersistentAndFlaggedTemporaryMatesOnly()
    {
        var manager = new MateManager(null);
        var persistent = new Mate();
        var survivingTemporary = new Mate { IsTemporarySummon = true };
        var despawningTemporary = new Mate
        {
            IsTemporarySummon = true,
            DespawnOnCreatorDeath = true
        };
        manager.TrackActiveMate(42, persistent);
        manager.TrackActiveMate(42, survivingTemporary);
        manager.TrackActiveMate(42, despawningTemporary);

        var selected = manager.GetMatesToRemoveOnOwnerDeath(42);

        await Assert.That(selected).Contains(persistent);
        await Assert.That(selected).Contains(despawningTemporary);
        await Assert.That(selected).DoesNotContain(survivingTemporary);
    }

    [Test]
    [Arguments(true, 100, false, false, true)]
    [Arguments(true, 0, false, false, false)]
    [Arguments(true, 0, true, true, false)]
    [Arguments(true, 0, true, false, true)]
    [Arguments(false, 100, false, false, false)]
    [Arguments(false, 100, true, false, false)]
    public async Task CanSpawnTrackedMate_RejectsLateDeathAndLogoutRaces(
        bool ownerIsOnline,
        int ownerHp,
        bool isTemporarySummon,
        bool despawnOnCreatorDeath,
        bool expected)
    {
        var result = MateManager.CanSpawnTrackedMate(
            ownerIsOnline,
            ownerHp,
            isTemporarySummon,
            despawnOnCreatorDeath);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task TrackingTemporaryMate_ReplacesSameTemplateButKeepsDistinctTemplates()
    {
        var manager = new MateManager(null);
        var first = new Mate { TemplateId = 100 };
        var distinct = new Mate { TemplateId = 200 };
        var replacement = new Mate { TemplateId = 100 };
        await Assert.That(manager.TrackTemporaryMate(42, first)).IsEmpty();
        await Assert.That(manager.TrackTemporaryMate(42, distinct)).IsEmpty();

        var replaced = manager.TrackTemporaryMate(42, replacement);
        var active = manager.GetActiveMates(42);

        await Assert.That(replaced).HasSingleItem();
        await Assert.That(replaced[0]).IsSameReferenceAs(first);
        await Assert.That(active).Contains(distinct);
        await Assert.That(active).Contains(replacement);
        await Assert.That(active).DoesNotContain(first);
        manager.CompleteMateRemoval(first);
    }

    [Test]
    public async Task MateLifecycle_RemovalWaitsForSpawnAndCleanupRunsOnce()
    {
        var mate = new Mate();
        var spawnEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSpawn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalAttempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCount = 0;
        var shouldDeleteWorldObject = false;

        var spawnTask = Task.Run(() => mate.TryRunSpawnLifecycle(
            () => { },
            () =>
            {
                spawnEntered.SetResult(true);
                finishSpawn.Task.GetAwaiter().GetResult();
            }));
        await spawnEntered.Task;

        var removalTask = Task.Run(() =>
        {
            removalAttempted.SetResult(true);
            return mate.TryRunDespawnLifecycle(shouldDelete =>
            {
                shouldDeleteWorldObject = shouldDelete;
                Interlocked.Increment(ref cleanupCount);
            });
        });
        await removalAttempted.Task;

        try
        {
            await Assert.That(removalTask.IsCompleted).IsFalse();
        }
        finally
        {
            finishSpawn.TrySetResult(true);
        }

        await Assert.That(await spawnTask).IsTrue();
        await Assert.That(await removalTask).IsTrue();
        await Assert.That(mate.TryRunDespawnLifecycle(_ => Interlocked.Increment(ref cleanupCount))).IsFalse();
        await Assert.That(cleanupCount).IsEqualTo(1);
        await Assert.That(shouldDeleteWorldObject).IsTrue();
        await Assert.That(mate.LifecycleState).IsEqualTo(MateLifecycleState.Removed);
    }

    [Test]
    public async Task MateLifecycle_WorldSpawnFailureCanBeCleanedOnlyOnce()
    {
        var mate = new Mate();
        var spawnFailed = false;
        try
        {
            mate.TryRunSpawnLifecycle(
                () => { },
                () => throw new InvalidOperationException("spawn failed"));
        }
        catch (InvalidOperationException)
        {
            spawnFailed = true;
        }

        var cleanupCount = 0;
        var shouldDeleteWorldObject = false;
        var firstCleanup = mate.TryRunDespawnLifecycle(shouldDelete =>
        {
            shouldDeleteWorldObject = shouldDelete;
            cleanupCount++;
        });
        var secondCleanup = mate.TryRunDespawnLifecycle(_ => cleanupCount++);

        await Assert.That(spawnFailed).IsTrue();
        await Assert.That(firstCleanup).IsTrue();
        await Assert.That(secondCleanup).IsFalse();
        await Assert.That(cleanupCount).IsEqualTo(1);
        await Assert.That(shouldDeleteWorldObject).IsTrue();
        await Assert.That(mate.LifecycleState).IsEqualTo(MateLifecycleState.Removed);
    }

    [Test]
    public async Task MateLifecycle_RetiringUnspawnedMateReleasesOnceAndPreventsLateSpawn()
    {
        var mate = new Mate();
        var objectIdReleaseCount = 0;
        var tlIdReleaseCount = 0;
        var spawnCount = 0;
        var shouldDeleteWorldObject = true;

        var removed = mate.TryRunDespawnLifecycle(shouldDelete =>
        {
            shouldDeleteWorldObject = shouldDelete;
            objectIdReleaseCount++;
            tlIdReleaseCount++;
        });
        var spawned = mate.TryRunSpawnLifecycle(() => { }, () => spawnCount++);
        var removedAgain = mate.TryRunDespawnLifecycle(_ =>
        {
            objectIdReleaseCount++;
            tlIdReleaseCount++;
        });

        await Assert.That(removed).IsTrue();
        await Assert.That(removedAgain).IsFalse();
        await Assert.That(spawned).IsFalse();
        await Assert.That(objectIdReleaseCount).IsEqualTo(1);
        await Assert.That(tlIdReleaseCount).IsEqualTo(1);
        await Assert.That(spawnCount).IsEqualTo(0);
        await Assert.That(shouldDeleteWorldObject).IsFalse();
        await Assert.That(mate.LifecycleState).IsEqualTo(MateLifecycleState.Removed);
    }

    [Test]
    public async Task MateLifecycle_SendFailureSkipsWorldDeleteButStillCleansOnlyOnce()
    {
        var mate = new Mate();
        var sendFailed = false;
        var worldSpawnCount = 0;
        try
        {
            mate.TryRunSpawnLifecycle(
                () => throw new InvalidOperationException("send failed"),
                () => worldSpawnCount++);
        }
        catch (InvalidOperationException)
        {
            sendFailed = true;
        }

        var cleanupCount = 0;
        var shouldDeleteWorldObject = true;
        var firstCleanup = mate.TryRunDespawnLifecycle(shouldDelete =>
        {
            shouldDeleteWorldObject = shouldDelete;
            cleanupCount++;
        });
        var secondCleanup = mate.TryRunDespawnLifecycle(_ => cleanupCount++);

        await Assert.That(sendFailed).IsTrue();
        await Assert.That(worldSpawnCount).IsEqualTo(0);
        await Assert.That(firstCleanup).IsTrue();
        await Assert.That(secondCleanup).IsFalse();
        await Assert.That(cleanupCount).IsEqualTo(1);
        await Assert.That(shouldDeleteWorldObject).IsFalse();
        await Assert.That(mate.LifecycleState).IsEqualTo(MateLifecycleState.Removed);
    }

    [Test]
    public async Task PersistentStateCopy_PreservesRuntimeMateStateBeforeDespawn()
    {
        var updatedAt = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var target = new MateDb();
        var source = new Mate
        {
            Hp = 101,
            Mp = 202,
            Level = 30,
            Experience = 303,
            Mileage = 404,
            Name = "Gallant"
        };

        CharacterMates.CopyPersistentMateState(target, source, updatedAt);

        await Assert.That(target.Hp).IsEqualTo(101);
        await Assert.That(target.Mp).IsEqualTo(202);
        await Assert.That(target.Level).IsEqualTo((ushort)30);
        await Assert.That(target.Xp).IsEqualTo(303);
        await Assert.That(target.Mileage).IsEqualTo(404);
        await Assert.That(target.Name).IsEqualTo("Gallant");
        await Assert.That(target.UpdatedAt).IsEqualTo(updatedAt);
    }
}
