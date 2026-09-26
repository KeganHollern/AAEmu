using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Schedules;

using Microsoft.Extensions.Time.Testing;

using DayOfWeek = AAEmu.Game.Models.Game.Schedules.DayOfWeek;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class QuestScheduleManagerTests
{
    [Test]
    [Arguments("2026-09-26T09:59:59Z", false)]
    [Arguments("2026-09-26T10:00:00Z", true)]
    [Arguments("2026-09-26T11:59:59Z", true)]
    [Arguments("2026-09-26T12:00:00Z", false)]
    [Arguments("2026-09-26T13:59:59Z", false)]
    [Arguments("2026-09-26T14:00:00Z", true)]
    [Arguments("2026-09-26T15:59:59Z", true)]
    [Arguments("2026-09-26T16:00:00Z", false)]
    public async Task CanAcceptQuest_UsesEitherLinkedWindowAndExcludesBothEndBoundaries(string now, bool expected)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(now));
        var manager = new GameScheduleManager(null, clock);
        manager.LoadGameSchedules(new Dictionary<int, GameSchedules>
        {
            [1] = Window(1, 10, 12),
            [2] = Window(2, 14, 16)
        });
        manager.LoadGameScheduleQuests(new Dictionary<int, GameScheduleQuests>
        {
            [1] = new() { Id = 1, QuestId = 5823, GameScheduleId = 1 },
            [2] = new() { Id = 2, QuestId = 5823, GameScheduleId = 2 }
        });

        await Assert.That(manager.CanAcceptQuest(5823)).IsEqualTo(expected);
        await Assert.That(manager.CanAcceptQuest(101)).IsTrue();
    }

    [Test]
    public async Task CanAcceptQuest_MissingScheduleDoesNotGrantAccessOrHideAnActiveAlternative()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-26T11:00:00Z"));
        var manager = new GameScheduleManager(null, clock);
        manager.LoadGameSchedules(new Dictionary<int, GameSchedules> { [1] = Window(1, 10, 12) });
        manager.LoadGameScheduleQuests(new Dictionary<int, GameScheduleQuests>
        {
            [1] = new() { Id = 1, QuestId = 5823, GameScheduleId = 26 },
            [2] = new() { Id = 2, QuestId = 5823, GameScheduleId = 27 },
            [3] = new() { Id = 3, QuestId = 101, GameScheduleId = 26 },
            [4] = new() { Id = 4, QuestId = 101, GameScheduleId = 1 }
        });

        await Assert.That(manager.CanAcceptQuest(5823)).IsFalse();
        await Assert.That(manager.CanAcceptQuest(101)).IsTrue();
        clock.Advance(TimeSpan.FromHours(1));
        await Assert.That(manager.CanAcceptQuest(101)).IsFalse();
    }

    [Test]
    public async Task CanAcceptQuest_WeeklyWindowUsesUtcAndResumesTheNextWeek()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-27T23:59:59Z"));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(9), "test", "test"));
        var manager = new GameScheduleManager(null, clock);
        manager.LoadGameSchedules(new Dictionary<int, GameSchedules>
        {
            [108] = new() { Id = 108, DayOfWeekId = DayOfWeek.Monday, EndTime = 23, EndTimeMin = 59 }
        });
        manager.LoadGameScheduleQuests(new Dictionary<int, GameScheduleQuests>
        {
            [1] = new() { Id = 1, QuestId = 6361, GameScheduleId = 108 }
        });

        await Assert.That(manager.CanAcceptQuest(6361)).IsFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(manager.CanAcceptQuest(6361)).IsTrue();
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        await Assert.That(manager.CanAcceptQuest(6361)).IsFalse();
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-05T00:00:00Z"));
        await Assert.That(manager.CanAcceptQuest(6361)).IsTrue();
    }

    [Test]
    public async Task ReloadQuestAssociations_ReplacesThePreviousIndex()
    {
        var manager = new GameScheduleManager(null, TimeProvider.System);
        manager.LoadGameScheduleQuests(new Dictionary<int, GameScheduleQuests>
        {
            [1] = new() { Id = 1, QuestId = 5823, GameScheduleId = 26 }
        });
        await Assert.That(manager.CanAcceptQuest(5823)).IsFalse();
        manager.LoadGameScheduleQuests([]);
        await Assert.That(manager.CanAcceptQuest(5823)).IsTrue();
    }

    private static GameSchedules Window(int id, int start, int end) => new()
    {
        Id = id,
        StYear = 2026, StMonth = 9, StDay = 26, StHour = start,
        EdYear = 2026, EdMonth = 9, EdDay = 26, EdHour = end
    };
}
