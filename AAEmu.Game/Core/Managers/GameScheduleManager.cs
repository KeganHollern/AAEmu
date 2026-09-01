using System.Globalization;

using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Schedules;

using NCrontab;

using NLog;

using static System.String;

using DayOfWeek = AAEmu.Game.Models.Game.Schedules.DayOfWeek;

namespace AAEmu.Game.Core.Managers;

public class GameScheduleManager(
    IGameDataManager gameDataManager, // ensures GameDataManager.Load() runs before this Load()
    TimeProvider timeProvider
) : Singleton<GameScheduleManager>, IGameScheduleManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly IGameDataManager _gameDataManager = gameDataManager;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private bool _loaded = false;
    private Dictionary<int, GameSchedules> _gameSchedules = []; // GameScheduleId, GameSchedules
    private Dictionary<int, GameScheduleSpawners> _gameScheduleSpawners = [];
    private Dictionary<int, List<int>> _gameScheduleSpawnerIds = [];
    private Dictionary<int, GameScheduleDoodads> _gameScheduleDoodads = [];
    private Dictionary<int, List<int>> _gameScheduleDoodadIds = [];
    private Dictionary<int, GameScheduleQuests> _gameScheduleQuests = [];
    private List<int> GameScheduleId { get; set; }

    public void Load()
    {
        if (_loaded)
            return;

        Logger.Info("Loading schedules...");

        SchedulesGameData.Instance.PostLoad();

        Logger.Info("Loaded schedules");

        _loaded = true;
    }

    public void LoadGameSchedules(Dictionary<int, GameSchedules> gameSchedules)
    {
        //_gameSchedules = new Dictionary<int, GameSchedules>();
        //foreach (var gs in gameSchedules)
        //{
        //    _gameSchedules.TryAdd(gs.Key, gs.Value);
        //}
        _gameSchedules = gameSchedules;
    }

    public void LoadGameScheduleSpawners(Dictionary<int, GameScheduleSpawners> gameScheduleSpawners)
    {
        _gameScheduleSpawners = gameScheduleSpawners;
        _gameScheduleSpawnerIds = [];
        foreach (var gameScheduleSpawner in _gameScheduleSpawners.Values)
        {
            if (!_gameScheduleSpawnerIds.TryGetValue(gameScheduleSpawner.SpawnerId, out var gameScheduleIds))
            {
                _gameScheduleSpawnerIds.Add(gameScheduleSpawner.SpawnerId, [gameScheduleSpawner.GameScheduleId]);
            }
            else
            {
                gameScheduleIds.Add(gameScheduleSpawner.GameScheduleId);
            }
        }
    }

    public void LoadGameScheduleDoodads(Dictionary<int, GameScheduleDoodads> gameScheduleDoodads)
    {
        _gameScheduleDoodads = gameScheduleDoodads;
        _gameScheduleDoodadIds = [];
        foreach (var gameScheduleDoodad in _gameScheduleDoodads.Values)
        {
            if (!_gameScheduleDoodadIds.TryGetValue(gameScheduleDoodad.DoodadId, out var gameScheduleIds))
            {
                _gameScheduleDoodadIds.Add(gameScheduleDoodad.DoodadId, [gameScheduleDoodad.GameScheduleId]);
            }
            else
            {
                gameScheduleIds.Add(gameScheduleDoodad.GameScheduleId);
            }
        }
    }

    public void LoadGameScheduleQuests(Dictionary<int, GameScheduleQuests> gameScheduleQuests)
    {
        _gameScheduleQuests = gameScheduleQuests;
    }

    public bool CheckSpawnerInScheduleSpawners(int spawnerId)
    {
        return _gameScheduleSpawnerIds.ContainsKey(spawnerId);
    }

    public bool CheckDoodadInScheduleSpawners(int spawnerId)
    {
        return _gameScheduleDoodadIds.ContainsKey(spawnerId);
    }

    public bool CheckSpawnerInGameSchedules(int spawnerId)
    {
        var res = CheckSpawnerScheduler(spawnerId);
        return res;
    }

    public bool CheckDoodadInGameSchedules(uint doodadId)
    {
        var res = CheckDoodadScheduler((int)doodadId);
        return res;
    }

    private bool CheckSpawnerScheduler(int spawnerId)
    {
        var res = false;
        foreach (var gameScheduleId in _gameScheduleSpawnerIds[spawnerId])
        {
            if (_gameSchedules.TryGetValue(gameScheduleId, out var gs))
            {
                res = true;
            }
        }

        return res;
    }

    private bool CheckDoodadScheduler(int doodadId)
    {
        var res = false;
        foreach (var gameScheduleId in _gameScheduleDoodadIds[doodadId])
        {
            if (_gameSchedules.TryGetValue(gameScheduleId, out var gs))
            {
                res = true;
            }
        }

        return res;
    }

    public enum PeriodStatus
    {
        NotFound,   // If doodadId or spawnerId not found
        NotStarted, // The period has not started
        InProgress, // Period in progress
        Ended       // The period has ended
    }

    /// <summary>
    /// Returns enum that shows the overall period status for all GameSchedules associated with spawnerId.
    /// </summary>
    /// <param name="spawnerId"></param>
    /// <returns></returns>
    public PeriodStatus GetPeriodStatusNpc(int spawnerId)
    {
        if (!_gameScheduleSpawnerIds.TryGetValue(spawnerId, out var ids))
            return PeriodStatus.NotFound; // If spawnerId is not found

        return CheckPeriodStatus(ids);
    }

    /// <summary>
    /// Returns an enum that shows the overall period status for all GameSchedules associated with the specified doodadId.
    /// If the doodadId is not found, returns <see cref="PeriodStatus.NotFound"/>.
    /// </summary>
    /// <param name="doodadId">The ID of the doodad to check.</param>
    /// <returns>The overall period status.</returns>
    public PeriodStatus GetPeriodStatusDoodad(int doodadId)
    {
        if (!_gameScheduleDoodadIds.TryGetValue(doodadId, out var ids))
            return PeriodStatus.NotFound; // If doodadId is not found

        return CheckPeriodStatus(ids);
    }

    /// <summary>
    /// Checks the period status for a list of game schedule IDs.
    /// </summary>
    /// <param name="ids">The list of game schedule IDs to check.</param>
    /// <returns>The overall period status.</returns>
    private PeriodStatus CheckPeriodStatus(List<int> ids)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var hasSchedule = false;
        var hasNotEnded = false;

        foreach (var gameScheduleId in ids)
        {
            if (_gameSchedules.TryGetValue(gameScheduleId, out var gs))
            {
                hasSchedule = true;
                var (isActive, hasEnded) = CheckData(gs, now);
                if (isActive)
                    return PeriodStatus.InProgress;
                if (!hasEnded)
                    hasNotEnded = true;
            }
        }

        return hasSchedule && !hasNotEnded ? PeriodStatus.Ended : PeriodStatus.NotStarted;
    }

    public TimeSpan GetRemainingTime(int spawnerId, bool start = true)
    {
        if (!_gameScheduleSpawnerIds.TryGetValue(spawnerId, out var gameScheduleIds))
            return TimeSpan.Zero;

        return GetRemainingTime(gameScheduleIds, start);
    }

    public TimeSpan GetDoodadRemainingTime(int doodadId, bool start = true)
    {
        if (!_gameScheduleDoodadIds.TryGetValue(doodadId, out var gameScheduleIds))
            return TimeSpan.Zero;

        return GetRemainingTime(gameScheduleIds, start);
    }

    private TimeSpan GetRemainingTime(IReadOnlyList<int> gameScheduleIds, bool start)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var occurrence = FindNextOccurrence(gameScheduleIds, start, now);
        return occurrence.HasValue ? occurrence.Value.OccursAt - now : TimeSpan.MaxValue;
    }

    public bool HasGameScheduleSpawnersData(uint spawnerTemplateId)
    {
        return _gameScheduleSpawners.Values.Any(gss => gss.SpawnerId == spawnerTemplateId);
    }

    public bool GetGameScheduleDoodadsData(uint doodadId)
    {
        GameScheduleId = [];
        foreach (var gsd in _gameScheduleDoodads.Values)
        {
            if (gsd.DoodadId != doodadId) { continue; }
            GameScheduleId.Add(gsd.GameScheduleId);
        }
        return GameScheduleId.Count != 0;
    }

    public bool GetGameScheduleQuestsData(uint questId)
    {
        GameScheduleId = [];
        foreach (var gsq in _gameScheduleQuests.Values)
        {
            if (gsq.QuestId != questId) { continue; }
            GameScheduleId.Add(gsq.GameScheduleId);
        }
        return GameScheduleId.Count != 0;
    }

    private ScheduleOccurrence? FindNextOccurrence(IReadOnlyList<int> gameScheduleIds, bool start, DateTime now)
    {
        ScheduleOccurrence? next = null;

        foreach (var gameScheduleId in gameScheduleIds)
        {
            if (!_gameSchedules.TryGetValue(gameScheduleId, out var gameSchedule))
                continue;

            var candidate = GetNextOccurrence(gameScheduleId, gameSchedule, start, now);
            if (!candidate.HasValue)
                continue;

            if (!next.HasValue || candidate.Value.OccursAt < next.Value.OccursAt ||
                (candidate.Value.OccursAt == next.Value.OccursAt && candidate.Value.ScheduleId < next.Value.ScheduleId))
            {
                next = candidate;
            }
        }

        return next;
    }

    internal static (bool isActive, bool hasEnded) CheckData(GameSchedules value, DateTime now)
    {
        var absoluteStart = GetAbsoluteBoundary(value, true);
        var absoluteEnd = GetAbsoluteBoundary(value, false);

        if (absoluteEnd.HasValue && now >= absoluteEnd.Value)
            return (false, true);
        if (absoluteStart.HasValue && now < absoluteStart.Value)
            return (false, false);

        return (IsInRecurringWindow(value, now), false);
    }

    private static ScheduleOccurrence? GetNextOccurrence(int scheduleId, GameSchedules value, bool start, DateTime now)
    {
        var absoluteEnd = GetAbsoluteBoundary(value, false);
        if (absoluteEnd.HasValue && now >= absoluteEnd.Value)
            return null;

        return HasRecurrence(value)
            ? GetNextRecurringOccurrence(scheduleId, value, start, now)
            : GetNextAbsoluteOccurrence(scheduleId, value, start, now);
    }

    private static ScheduleOccurrence? GetNextAbsoluteOccurrence(int scheduleId, GameSchedules value, bool start, DateTime now)
    {
        var boundary = GetAbsoluteBoundary(value, start);
        if (!boundary.HasValue || boundary.Value <= now)
            return null;

        return new ScheduleOccurrence(boundary.Value, scheduleId);
    }

    private static ScheduleOccurrence? GetNextRecurringOccurrence(int scheduleId, GameSchedules value, bool start, DateTime now)
    {
        var absoluteStart = GetAbsoluteBoundary(value, true);
        var absoluteEnd = GetAbsoluteBoundary(value, false);
        var cronExpression = GetCronExpression(value, start);
        var schedule = CrontabSchedule.Parse(cronExpression, TaskManager.s_crontabScheduleParseOptions);
        var searchFrom = absoluteStart.HasValue && absoluteStart.Value > now
            ? absoluteStart.Value.AddTicks(-1)
            : now;
        DateTime? nextTime = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var candidate = schedule.GetNextOccurrence(searchFrom);
            var withinEnd = !absoluteEnd.HasValue || (start ? candidate < absoluteEnd.Value : candidate <= absoluteEnd.Value);
            if (!withinEnd)
                break;

            var isTransition = start
                ? IsInRecurringWindow(value, candidate)
                : candidate > DateTime.MinValue &&
                  (!absoluteStart.HasValue || candidate.AddTicks(-1) >= absoluteStart.Value) &&
                  IsInRecurringWindow(value, candidate.AddTicks(-1));
            if (isTransition)
            {
                nextTime = candidate;
                break;
            }

            searchFrom = candidate;
        }

        if (start && absoluteStart.HasValue && absoluteStart.Value > now &&
            (!absoluteEnd.HasValue || absoluteStart.Value < absoluteEnd.Value) &&
            IsInRecurringWindow(value, absoluteStart.Value) &&
            (!nextTime.HasValue || absoluteStart.Value < nextTime.Value))
        {
            nextTime = absoluteStart.Value;
        }

        if (!start && absoluteEnd.HasValue && absoluteEnd.Value > now)
        {
            var justBeforeEnd = absoluteEnd.Value.AddTicks(-1);
            if ((!absoluteStart.HasValue || justBeforeEnd >= absoluteStart.Value) &&
                IsInRecurringWindow(value, justBeforeEnd) &&
                (!nextTime.HasValue || absoluteEnd.Value < nextTime.Value))
            {
                nextTime = absoluteEnd.Value;
            }
        }

        return nextTime.HasValue
            ? new ScheduleOccurrence(nextTime.Value, scheduleId)
            : null;
    }

    private static bool IsInRecurringWindow(GameSchedules value, DateTime now)
    {
        if (!HasRecurringClock(value))
            return IsScheduledWeekday(value, now.DayOfWeek);

        var currentTime = now.TimeOfDay;
        var startTime = GetRecurringTime(value, true);
        var endTime = GetRecurringTime(value, false);

        if (startTime < endTime)
        {
            return IsScheduledWeekday(value, now.DayOfWeek) &&
                   currentTime >= startTime && currentTime < endTime;
        }

        var previousDay = (System.DayOfWeek)(((int)now.DayOfWeek + 6) % 7);
        return (IsScheduledWeekday(value, now.DayOfWeek) && currentTime >= startTime) ||
               (IsScheduledWeekday(value, previousDay) && currentTime < endTime);
    }

    private static bool IsScheduledWeekday(GameSchedules value, System.DayOfWeek dayOfWeek)
    {
        var cronDay = GetCronDayOfWeek(value.DayOfWeekId);
        return !cronDay.HasValue || cronDay.Value == (int)dayOfWeek;
    }

    private static bool HasRecurrence(GameSchedules value)
    {
        return HasRecurringClock(value) || GetCronDayOfWeek(value.DayOfWeekId).HasValue;
    }

    private static bool HasRecurringClock(GameSchedules value)
    {
        return value.StartTime != 0 || value.StartTimeMin != 0 || value.EndTime != 0 || value.EndTimeMin != 0;
    }

    private static TimeSpan GetRecurringTime(GameSchedules value, bool start)
    {
        return start
            ? new TimeSpan(value.StartTime, value.StartTimeMin, 0)
            : new TimeSpan(value.EndTime, value.EndTimeMin, 0);
    }

    private static DateTime? GetAbsoluteBoundary(GameSchedules value, bool start)
    {
        var year = start ? value.StYear : value.EdYear;
        var month = start ? value.StMonth : value.EdMonth;
        var day = start ? value.StDay : value.EdDay;
        if (year <= 0 || month <= 0 || day <= 0)
            return null;

        var hour = start ? value.StHour : value.EdHour;
        var minute = start ? value.StMin : value.EdMin;
        return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
    }

    internal static string GetCronExpression(GameSchedules value, bool start = true)
    {
        if (!HasRecurrence(value))
            return Empty;

        var hour = start ? value.StartTime : value.EndTime;
        var minute = start ? value.StartTimeMin : value.EndTimeMin;
        var dayOfWeek = GetCronDayOfWeek(value.DayOfWeekId);
        if (!start && dayOfWeek.HasValue && GetRecurringTime(value, false) <= GetRecurringTime(value, true))
            dayOfWeek = (dayOfWeek.Value + 1) % 7;

        var dayOfWeekField = dayOfWeek.HasValue ? FormatCronValue(dayOfWeek.Value) : "*";
        return $"0 {FormatCronValue(minute)} {FormatCronValue(hour)} * * {dayOfWeekField}";
    }

    private static int? GetCronDayOfWeek(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Sunday => 0,
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 3,
            DayOfWeek.Thursday => 4,
            DayOfWeek.Friday => 5,
            DayOfWeek.Saturday => 6,
            _ => null
        };
    }

    private static string FormatCronValue(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private readonly record struct ScheduleOccurrence(DateTime OccursAt, int ScheduleId);
}
