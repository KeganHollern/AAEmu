using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Schedules;

using Microsoft.Extensions.Time.Testing;

using NCrontab;

using DayOfWeek = AAEmu.Game.Models.Game.Schedules.DayOfWeek;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class GameScheduleManagerTests
{
    private const int SpawnerId = 15776;
    private const int DoodadId = 6972;

    [Test]
    [Arguments("2026-09-07T22:00:00Z", GameScheduleManager.PeriodStatus.NotStarted)]
    [Arguments("2026-09-07T22:59:59Z", GameScheduleManager.PeriodStatus.NotStarted)]
    [Arguments("2026-09-07T23:00:00Z", GameScheduleManager.PeriodStatus.InProgress)]
    [Arguments("2026-09-08T00:59:59Z", GameScheduleManager.PeriodStatus.InProgress)]
    [Arguments("2026-09-08T01:00:00Z", GameScheduleManager.PeriodStatus.NotStarted)]
    [Arguments("2026-09-08T22:30:00Z", GameScheduleManager.PeriodStatus.NotStarted)]
    [Arguments("2026-09-14T12:00:00Z", GameScheduleManager.PeriodStatus.NotStarted)]
    [Arguments("2026-09-14T22:00:00Z", GameScheduleManager.PeriodStatus.InProgress)]
    [Arguments("2026-09-15T00:29:59Z", GameScheduleManager.PeriodStatus.InProgress)]
    [Arguments("2026-09-15T00:30:00Z", GameScheduleManager.PeriodStatus.Ended)]
    public async Task GetPeriodStatus_WeeklyOvernightWindow_ClipsToAbsoluteBoundsAcrossTwoRecurrences(
        string utcNow,
        GameScheduleManager.PeriodStatus expected)
    {
        var schedule = new GameSchedules
        {
            Id = 1,
            Name = "Bounded Monday overnight",
            DayOfWeekId = DayOfWeek.Monday,
            StartTime = 22,
            EndTime = 1,
            StYear = 2026,
            StMonth = 9,
            StDay = 7,
            StHour = 23,
            EdYear = 2026,
            EdMonth = 9,
            EdDay = 15,
            EdMin = 30
        };
        var manager = CreateManager(utcNow, [schedule]);

        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(expected);
        await Assert.That(manager.GetPeriodStatusDoodad(DoodadId)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetPeriodStatus_WeeklyAllDayWindow_UsesTheWholeMatchingDay()
    {
        var timeProvider = CreateTimeProvider("2026-09-05T00:00:00Z");
        var schedule = new GameSchedules
        {
            Id = 14,
            Name = "Saturday all day",
            DayOfWeekId = DayOfWeek.Saturday,
            StYear = 2015,
            StMonth = 7,
            StDay = 22,
            EdYear = 9999
        };
        var manager = CreateManager(timeProvider, [schedule]);

        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.InProgress);

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-05T23:59:59Z"));
        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.InProgress);

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-06T00:00:00Z"));
        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.NotStarted);

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-12T12:00:00Z"));
        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.InProgress);
    }

    [Test]
    public async Task GetCronExpression_UnspecifiedDayAndMonth_EmitsWildcardsAndParses()
    {
        var schedule = new GameSchedules
        {
            Id = 1,
            Name = "Daily window",
            DayOfWeekId = DayOfWeek.Invalid,
            StartTime = 4,
            EndTime = 6
        };

        var startExpression = GameScheduleManager.GetCronExpression(schedule);
        var endExpression = GameScheduleManager.GetCronExpression(schedule, false);
        var startCron = CrontabSchedule.Parse(startExpression, TaskManager.s_crontabScheduleParseOptions);
        var endCron = CrontabSchedule.Parse(endExpression, TaskManager.s_crontabScheduleParseOptions);
        var now = DateTimeOffset.Parse("2026-09-01T03:00:00Z").UtcDateTime;

        await Assert.That(startExpression).IsEqualTo("0 0 4 * * *");
        await Assert.That(endExpression).IsEqualTo("0 0 6 * * *");
        await Assert.That(startCron.GetNextOccurrence(now)).IsEqualTo(DateTimeOffset.Parse("2026-09-01T04:00:00Z").UtcDateTime);
        await Assert.That(endCron.GetNextOccurrence(now)).IsEqualTo(DateTimeOffset.Parse("2026-09-01T06:00:00Z").UtcDateTime);
    }

    [Test]
    public async Task NextOccurrence_BoundedWeeklyRecurrence_SeparatesValidityFromCronFields()
    {
        var timeProvider = CreateTimeProvider("2026-09-01T00:00:00Z");
        var schedule = new GameSchedules
        {
            Id = 6,
            Name = "Future bounded Monday window",
            DayOfWeekId = DayOfWeek.Monday,
            StartTime = 10,
            EndTime = 11,
            StYear = 2026,
            StMonth = 9,
            StDay = 17,
            StHour = 12,
            EdYear = 2026,
            EdMonth = 10,
            EdDay = 1,
            EdHour = 12
        };
        var manager = CreateManager(timeProvider, [schedule]);

        var startExpression = GameScheduleManager.GetCronExpression(schedule);
        var endExpression = GameScheduleManager.GetCronExpression(schedule, false);
        var startCron = CrontabSchedule.Parse(startExpression, TaskManager.s_crontabScheduleParseOptions);
        var endCron = CrontabSchedule.Parse(endExpression, TaskManager.s_crontabScheduleParseOptions);

        await Assert.That(startExpression).IsEqualTo("0 0 10 * * 1");
        await Assert.That(endExpression).IsEqualTo("0 0 11 * * 1");
        await Assert.That(startCron).IsNotNull();
        await Assert.That(endCron).IsNotNull();
        await Assert.That(manager.GetRemainingTime(SpawnerId)).IsEqualTo(TimeSpan.FromDays(20) + TimeSpan.FromHours(10));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId)).IsEqualTo(TimeSpan.FromDays(20) + TimeSpan.FromHours(10));
    }

    [Test]
    public async Task NextOccurrence_AbsoluteOnlySchedule_PreservesYearAndBoundaryClock()
    {
        var timeProvider = CreateTimeProvider("2026-09-01T00:00:00Z");
        var schedule = new GameSchedules
        {
            Id = 43,
            Name = "Future absolute window",
            DayOfWeekId = DayOfWeek.Invalid,
            StYear = 2099,
            StMonth = 1,
            StDay = 1,
            StHour = 6,
            StMin = 15,
            EdYear = 2099,
            EdMonth = 1,
            EdDay = 2,
            EdHour = 7,
            EdMin = 45
        };
        var manager = CreateManager(timeProvider, [schedule]);
        var expectedStart = DateTimeOffset.Parse("2099-01-01T06:15:00Z") - timeProvider.GetUtcNow();

        await Assert.That(GameScheduleManager.GetCronExpression(schedule)).IsEmpty();
        await Assert.That(manager.GetRemainingTime(SpawnerId)).IsEqualTo(expectedStart);
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId)).IsEqualTo(expectedStart);

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2099-01-01T06:15:00Z"));

        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.InProgress);
        await Assert.That(manager.GetRemainingTime(SpawnerId, false)).IsEqualTo(TimeSpan.FromHours(25.5));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId, false)).IsEqualTo(TimeSpan.FromHours(25.5));
    }

    [Test]
    public async Task NextOccurrence_BoundedDailyMinuteWindow_ClipsBothValidityBoundaries()
    {
        var timeProvider = CreateTimeProvider("2026-09-01T05:00:00Z");
        var schedule = new GameSchedules
        {
            Id = 1,
            Name = "Bounded daily minute window",
            DayOfWeekId = DayOfWeek.Invalid,
            StartTime = 4,
            StartTimeMin = 58,
            EndTime = 6,
            EndTimeMin = 59,
            StYear = 2026,
            StMonth = 9,
            StDay = 1,
            StHour = 5,
            StMin = 30,
            EdYear = 2026,
            EdMonth = 9,
            EdDay = 2,
            EdHour = 6
        };
        var manager = CreateManager(timeProvider, [schedule]);

        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.NotStarted);
        await Assert.That(manager.GetRemainingTime(SpawnerId)).IsEqualTo(TimeSpan.FromMinutes(30));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId)).IsEqualTo(TimeSpan.FromMinutes(30));
        await Assert.That(manager.GetRemainingTime(SpawnerId, false)).IsEqualTo(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(59));

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T07:00:00Z"));

        await Assert.That(manager.GetRemainingTime(SpawnerId)).IsEqualTo(TimeSpan.FromHours(21) + TimeSpan.FromMinutes(58));
        await Assert.That(manager.GetRemainingTime(SpawnerId, false)).IsEqualTo(TimeSpan.FromHours(23));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId, false)).IsEqualTo(TimeSpan.FromHours(23));

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-02T05:59:59Z"));
        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.InProgress);

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-02T06:00:00Z"));
        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.Ended);
    }

    [Test]
    public async Task GetCronExpression_WeeklyOvernightAndAllDayWindows_ShiftsEndToFollowingWeekday()
    {
        var overnight = new GameSchedules
        {
            Id = 1,
            Name = "Monday overnight",
            DayOfWeekId = DayOfWeek.Monday,
            StartTime = 22,
            EndTime = 1
        };
        var allDay = new GameSchedules
        {
            Id = 2,
            Name = "Saturday all day",
            DayOfWeekId = DayOfWeek.Saturday
        };

        await Assert.That(GameScheduleManager.GetCronExpression(overnight)).IsEqualTo("0 0 22 * * 1");
        await Assert.That(GameScheduleManager.GetCronExpression(overnight, false)).IsEqualTo("0 0 1 * * 2");
        await Assert.That(GameScheduleManager.GetCronExpression(allDay)).IsEqualTo("0 0 0 * * 6");
        await Assert.That(GameScheduleManager.GetCronExpression(allDay, false)).IsEqualTo("0 0 0 * * 0");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NextOccurrence_MultipleWindows_SelectsChronologicalMinimumRegardlessOfAssociationOrder(bool reverse)
    {
        var schedules = CreateDailyWindows();
        var associationOrder = schedules.Select(schedule => schedule.Id).ToList();
        if (reverse)
            associationOrder.Reverse();
        var timeProvider = CreateTimeProvider("2026-09-01T03:00:00Z");
        var manager = CreateManager(timeProvider, schedules, associationOrder);

        await Assert.That(manager.GetRemainingTime(SpawnerId)).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(manager.GetRemainingTime(SpawnerId, false)).IsEqualTo(TimeSpan.FromHours(3));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId)).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId, false)).IsEqualTo(TimeSpan.FromHours(3));

        timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-09-01T21:00:00Z"));

        await Assert.That(manager.GetRemainingTime(SpawnerId)).IsEqualTo(TimeSpan.FromHours(3));
        await Assert.That(manager.GetRemainingTime(SpawnerId, false)).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId)).IsEqualTo(TimeSpan.FromHours(3));
        await Assert.That(manager.GetDoodadRemainingTime(DoodadId, false)).IsEqualTo(TimeSpan.FromHours(1));
    }

    [Test]
    public async Task GetPeriodStatus_ExpiredAndFutureAssociations_ReturnsNotStarted()
    {
        var expired = new GameSchedules
        {
            Id = 1,
            Name = "Expired",
            DayOfWeekId = DayOfWeek.Invalid,
            StYear = 2026,
            StMonth = 1,
            StDay = 1,
            EdYear = 2026,
            EdMonth = 2,
            EdDay = 1
        };
        var future = new GameSchedules
        {
            Id = 2,
            Name = "Future",
            DayOfWeekId = DayOfWeek.Invalid,
            StYear = 2027,
            StMonth = 1,
            StDay = 1,
            EdYear = 2027,
            EdMonth = 2,
            EdDay = 1
        };
        var manager = CreateManager("2026-09-01T00:00:00Z", [expired, future]);

        await Assert.That(manager.GetPeriodStatusNpc(SpawnerId)).IsEqualTo(GameScheduleManager.PeriodStatus.NotStarted);
    }

    private static List<GameSchedules> CreateDailyWindows()
    {
        return
        [
            CreateDailyWindow(1, 0, 2),
            CreateDailyWindow(2, 4, 6),
            CreateDailyWindow(3, 8, 10),
            CreateDailyWindow(4, 12, 14),
            CreateDailyWindow(5, 16, 18),
            CreateDailyWindow(6, 20, 22)
        ];
    }

    private static GameSchedules CreateDailyWindow(int id, int startHour, int endHour)
    {
        return new GameSchedules
        {
            Id = id,
            Name = $"Daily window {id}",
            DayOfWeekId = DayOfWeek.Invalid,
            StartTime = startHour,
            EndTime = endHour
        };
    }

    private static FakeTimeProvider CreateTimeProvider(string utcNow)
    {
        return new FakeTimeProvider(DateTimeOffset.Parse(utcNow));
    }

    private static GameScheduleManager CreateManager(
        string utcNow,
        IReadOnlyList<GameSchedules> schedules,
        IReadOnlyList<int> associationOrder = null)
    {
        return CreateManager(CreateTimeProvider(utcNow), schedules, associationOrder);
    }

    private static GameScheduleManager CreateManager(
        FakeTimeProvider timeProvider,
        IReadOnlyList<GameSchedules> schedules,
        IReadOnlyList<int> associationOrder = null)
    {
        associationOrder ??= schedules.Select(schedule => schedule.Id).ToList();
        var manager = new GameScheduleManager(Mock.Of<IGameDataManager>().Object, timeProvider);
        manager.LoadGameSchedules(schedules.ToDictionary(schedule => schedule.Id));
        manager.LoadGameScheduleSpawners(associationOrder
            .Select((scheduleId, index) => new GameScheduleSpawners
            {
                Id = index + 1,
                GameScheduleId = scheduleId,
                SpawnerId = SpawnerId
            })
            .ToDictionary(link => link.Id));
        manager.LoadGameScheduleDoodads(associationOrder
            .Select((scheduleId, index) => new GameScheduleDoodads
            {
                Id = index + 1,
                GameScheduleId = scheduleId,
                DoodadId = DoodadId
            })
            .ToDictionary(link => link.Id));
        return manager;
    }
}
