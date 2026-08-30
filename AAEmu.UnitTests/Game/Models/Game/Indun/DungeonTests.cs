using AAEmu.Game.Models.Game.Indun;

namespace AAEmu.UnitTests.Game.Models.Game.Indun;

/// <summary>
/// Covers the pure empty-instance grace helper behind the abandoned-dungeon sweep (aaemu-cluster#92, #102).
/// The reuse-key and sweep integration paths depend on live singletons (WorldManager, ZoneManager,
/// IndunGameData, TickManager) inside the Dungeon constructor and cannot be exercised without heavy scaffolding.
/// </summary>
public class DungeonTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task IsPastEmptyGrace_NeverEmpty_IsFalse()
    {
        await Assert.That(Dungeon.IsPastEmptyGrace(null, Now)).IsFalse();
    }

    [Test]
    public async Task IsPastEmptyGrace_WithinGracePeriod_IsFalse()
    {
        await Assert.That(Dungeon.IsPastEmptyGrace(Now - TimeSpan.FromMinutes(9), Now)).IsFalse();
    }

    [Test]
    public async Task IsPastEmptyGrace_AtGraceBoundary_IsTrue()
    {
        // The 10 minute grace period is inclusive at the boundary
        await Assert.That(Dungeon.IsPastEmptyGrace(Now - TimeSpan.FromMinutes(10), Now)).IsTrue();
    }

    [Test]
    public async Task IsPastEmptyGrace_LongPastGrace_IsTrue()
    {
        await Assert.That(Dungeon.IsPastEmptyGrace(Now - TimeSpan.FromHours(1), Now)).IsTrue();
    }

    [Test]
    [Arguments(false, false, false, 10u, 10u, true, false, true)]
    [Arguments(false, false, false, 10u, 10u, false, true, true)]
    [Arguments(false, false, false, 10u, 10u, false, false, false)]
    [Arguments(false, false, false, 20u, 10u, true, false, false)]
    [Arguments(true, false, false, 10u, 10u, true, false, false)]
    [Arguments(false, true, false, 10u, 10u, true, false, false)]
    [Arguments(false, false, true, 10u, 10u, true, false, false)]
    public async Task CanPromoteActiveSoloToTeam_UsesOwnerAndActivityState(
        bool isDestroyed,
        bool isSystem,
        bool isTeamOwned,
        uint characterOwnerId,
        uint teamOwnerId,
        bool hasPlayers,
        bool hasEnterRequests,
        bool expected)
    {
        var result = Dungeon.CanPromoteActiveSoloToTeam(
            isDestroyed,
            isSystem,
            isTeamOwned,
            characterOwnerId,
            teamOwnerId,
            hasPlayers,
            hasEnterRequests);

        await Assert.That(result).IsEqualTo(expected);
    }
}
