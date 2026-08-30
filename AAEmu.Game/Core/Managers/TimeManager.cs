using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using Microsoft.Extensions.Options;
using NLog;

namespace AAEmu.Game.Core.Managers;

public class TimeManager : Singleton<TimeManager>, IObservable<float>, ITimeManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(10);
    private const float SecondsPerHour = 3600f;
    private const float HoursPerDay = 24f;

    private readonly object _lock = new();
    private readonly List<IObserver<float>> _observers = [];
    private readonly ITickManager _tickManager;
    private readonly IWorldManager _worldManager;
    private readonly TimeProvider _timeProvider;
    private readonly WorldTimeConfig _configuration;

    private bool _configured;
    private bool _work;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private float _clientSpeed = 1f / 600f;
    private float _time = 12f * SecondsPerHour;
    private float _lastTimeHours = 12f;
    private float _manualOffsetHours;
    private double _lastSourceHours;
    private double? _triggerHighWaterSourceHours;
    private float? _triggerHighWaterDisplayHours;

    public TimeManager(
        ITickManager tickManager,
        IWorldManager worldManager,
        TimeProvider timeProvider,
        IOptions<AppConfiguration> options)
    {
        _tickManager = tickManager;
        _worldManager = worldManager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var configuration = options?.Value.World?.Time ?? AppConfiguration.Instance.World?.Time ?? new WorldTimeConfig();
        _configuration = new WorldTimeConfig
        {
            Mode = configuration.Mode,
            TimeZoneId = configuration.TimeZoneId,
            AcceleratedDayLengthMinutes = configuration.AcceleratedDayLengthMinutes
        };
    }

    public TimeManager() : this(null, null, TimeProvider.System, null)
    {
    }

    /// <summary>
    /// Current game time in hours in the range [0, 24).
    /// </summary>
    public float GetTime
    {
        get
        {
            lock (_lock)
            {
                return _configured
                    ? CalculateClockSample(_timeProvider.GetUtcNow()).Hours
                    : _time / SecondsPerHour;
            }
        }
    }

    /// <summary>
    /// Game hours added by the r208022 client per real second.
    /// </summary>
    public float ClientSpeed
    {
        get
        {
            lock (_lock)
                return _clientSpeed;
        }
    }

    private ITickManager ActiveTickManager => _tickManager ?? TickManager.Instance;

    private IWorldManager ActiveWorldManager => _worldManager ?? WorldManager.Instance;

    public IDisposable Subscribe(IObserver<float> observer)
    {
        lock (_lock)
        {
            if (_observers.Contains(observer))
                return null;

            _observers.Add(observer);
            return new TimeSubscription(this, observer);
        }
    }

    public IDisposable Subscribe(GameConnection connection, IObserver<float> observer)
    {
        connection.SendPacket(new SCDetailedTimeOfDayPacket(GetTime, ClientSpeed));
        return Subscribe(observer);
    }

    public void Start()
    {
        ClockSample sample;
        lock (_lock)
        {
            if (_work)
                return;

            ConfigureClock();
            sample = CalculateClockSample(_timeProvider.GetUtcNow(), true);
            _time = sample.Hours * SecondsPerHour;
            _lastTimeHours = sample.Hours;
            _lastSourceHours = sample.SourceHours;
            _triggerHighWaterSourceHours = sample.SuppressEffectsUntilSourceHours;
            _triggerHighWaterDisplayHours = sample.SuppressEffectsUntilSourceHours is { } highWater
                ? MathF.BitDecrement(NormalizeHours(highWater))
                : null;
            _work = true;
        }

        ActiveTickManager.OnTick.Subscribe(Update, UpdateInterval, true);
        Logger.Info(
            "World clock started: mode={0}, hour={1:F3}, speed={2:G9}, timeZone={3}",
            _configuration.Mode,
            sample.Hours,
            ClientSpeed,
            _configuration.Mode == WorldTimeMode.TimeZone ? _timeZone.Id : "n/a");
    }

    public float Get()
    {
        return GetTime * SecondsPerHour;
    }

    public bool Set(float hours)
    {
        if (!float.IsFinite(hours))
            throw new ArgumentOutOfRangeException(nameof(hours));

        float oldHours;
        float newHours;
        List<IObserver<float>> observers;
        lock (_lock)
        {
            ConfigureClock();
            if (_configuration.Mode == WorldTimeMode.TimeZone)
                return false;

            var utcNow = _timeProvider.GetUtcNow();
            var currentSample = CalculateClockSample(utcNow);
            oldHours = currentSample.Hours;
            var baseHours = CalculateBaseClockSample(utcNow).Hours;
            newHours = NormalizeHours(hours);
            _manualOffsetHours = NormalizeHours(newHours - baseHours);
            _time = newHours * SecondsPerHour;
            _lastTimeHours = newHours;
            _lastSourceHours = currentSample.SourceHours;
            _triggerHighWaterSourceHours = null;
            _triggerHighWaterDisplayHours = null;
            observers = [.. _observers];
        }

        NotifyObservers(observers, newHours);
        OnTimeOfDayChange(newHours, oldHours);
        return true;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_work)
                return;
            _work = false;
        }

        ActiveTickManager.OnTick.UnSubscribe(Update);
    }

    internal void Update(TimeSpan _)
    {
        ClockSample sample;
        float oldHours;
        float oldHoursForEffects;
        bool processWorldEffects;
        List<IObserver<float>> observers;

        lock (_lock)
        {
            if (!_work)
                return;

            sample = CalculateClockSample(_timeProvider.GetUtcNow());
            oldHours = _lastTimeHours;
            oldHoursForEffects = oldHours;
            processWorldEffects = TryGetWorldEffectWindow(sample, oldHours, ref oldHoursForEffects);

            _time = sample.Hours * SecondsPerHour;
            _lastTimeHours = sample.Hours;
            _lastSourceHours = sample.SourceHours;
            observers = [.. _observers];
        }

        NotifyObservers(observers, sample.Hours);
        if (processWorldEffects)
            OnTimeOfDayChange(sample.Hours, oldHoursForEffects);
    }

    private void ConfigureClock()
    {
        if (_configured)
            return;

        switch (_configuration.Mode)
        {
            case WorldTimeMode.Accelerated:
                if (!double.IsFinite(_configuration.AcceleratedDayLengthMinutes) ||
                    _configuration.AcceleratedDayLengthMinutes <= 0d)
                {
                    throw new InvalidOperationException(
                        "World.Time.AcceleratedDayLengthMinutes must be a positive finite number.");
                }

                _clientSpeed = (float)(HoursPerDay / (_configuration.AcceleratedDayLengthMinutes * 60d));
                if (!float.IsFinite(_clientSpeed) || _clientSpeed <= 0f)
                    throw new InvalidOperationException("The configured accelerated world clock speed is invalid.");
                break;

            case WorldTimeMode.TimeZone:
                if (string.IsNullOrWhiteSpace(_configuration.TimeZoneId))
                    throw new InvalidOperationException("World.Time.TimeZoneId must not be empty.");

                try
                {
                    _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_configuration.TimeZoneId);
                }
                catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    throw new InvalidOperationException(
                        $"World.Time.TimeZoneId '{_configuration.TimeZoneId}' is not available.",
                        exception);
                }

                _clientSpeed = 1f / SecondsPerHour;
                break;

            default:
                throw new InvalidOperationException($"World.Time.Mode '{_configuration.Mode}' is not supported.");
        }

        _configured = true;
    }

    private ClockSample CalculateClockSample(DateTimeOffset utcNow, bool detectRepeatedPeriod = false)
    {
        var sample = CalculateBaseClockSample(utcNow, detectRepeatedPeriod);
        return sample with { Hours = NormalizeHours(sample.Hours + _manualOffsetHours) };
    }

    private ClockSample CalculateBaseClockSample(DateTimeOffset utcNow, bool detectRepeatedPeriod = false)
    {
        if (_configuration.Mode == WorldTimeMode.TimeZone)
        {
            var localNow = TimeZoneInfo.ConvertTime(utcNow, _timeZone);
            var localDate = DateOnly.FromDateTime(localNow.Date);
            var sourceHours = localDate.DayNumber * (double)HoursPerDay + localNow.TimeOfDay.TotalHours;
            double? suppressEffectsUntilSourceHours = null;
            var localDateTime = DateTime.SpecifyKind(localNow.DateTime, DateTimeKind.Unspecified);
            if (detectRepeatedPeriod &&
                _timeZone.IsAmbiguousTime(localDateTime) &&
                localNow.Offset == _timeZone.GetAmbiguousTimeOffsets(localDateTime).Min())
            {
                var repeatedPeriodEnd = FindAmbiguousPeriodEnd(localDateTime);
                var endDate = DateOnly.FromDateTime(repeatedPeriodEnd.Date);
                suppressEffectsUntilSourceHours =
                    endDate.DayNumber * (double)HoursPerDay + repeatedPeriodEnd.TimeOfDay.TotalHours;
            }
            return new ClockSample(
                (float)localNow.TimeOfDay.TotalHours,
                sourceHours,
                suppressEffectsUntilSourceHours);
        }

        var dayLengthSeconds = _configuration.AcceleratedDayLengthMinutes * 60d;
        var unixSeconds = utcNow.ToUnixTimeMilliseconds() / 1000d;
        var position = unixSeconds % dayLengthSeconds;
        if (position < 0d)
            position += dayLengthSeconds;
        return new ClockSample(
            (float)(position / dayLengthSeconds * HoursPerDay),
            unixSeconds / dayLengthSeconds * HoursPerDay,
            null);
    }

    private DateTime FindAmbiguousPeriodEnd(DateTime ambiguousLocalTime)
    {
        var lowerTicks = ambiguousLocalTime.Ticks;
        var upper = ambiguousLocalTime.AddHours(1);
        for (var hours = 1; _timeZone.IsAmbiguousTime(upper) && hours < 24; hours++)
            upper = upper.AddHours(1);

        if (_timeZone.IsAmbiguousTime(upper))
            throw new InvalidTimeZoneException($"Time zone '{_timeZone.Id}' has an invalid ambiguous period.");

        var upperTicks = upper.Ticks;
        while (upperTicks - lowerTicks > 1)
        {
            var middleTicks = lowerTicks + (upperTicks - lowerTicks) / 2;
            var middle = new DateTime(middleTicks, DateTimeKind.Unspecified);
            if (_timeZone.IsAmbiguousTime(middle))
                lowerTicks = middleTicks;
            else
                upperTicks = middleTicks;
        }

        return new DateTime(upperTicks, DateTimeKind.Unspecified);
    }

    private bool TryGetWorldEffectWindow(ClockSample sample, float oldHours, ref float oldHoursForEffects)
    {
        if (sample.SourceHours < _lastSourceHours)
        {
            if (_triggerHighWaterSourceHours == null || _lastSourceHours > _triggerHighWaterSourceHours)
            {
                _triggerHighWaterSourceHours = _lastSourceHours;
                _triggerHighWaterDisplayHours = oldHours;
            }
            return false;
        }

        if (_triggerHighWaterSourceHours == null)
            return true;

        if (sample.SourceHours < _triggerHighWaterSourceHours.Value)
            return false;

        oldHoursForEffects = _triggerHighWaterDisplayHours ?? oldHours;
        _triggerHighWaterSourceHours = null;
        _triggerHighWaterDisplayHours = null;
        return true;
    }

    private static float NormalizeHours(float hours)
    {
        var normalized = hours % HoursPerDay;
        return normalized < 0f ? normalized + HoursPerDay : normalized;
    }

    private static float NormalizeHours(double hours)
    {
        var normalized = hours % HoursPerDay;
        return (float)(normalized < 0d ? normalized + HoursPerDay : normalized);
    }

    private static void NotifyObservers(IEnumerable<IObserver<float>> observers, float time)
    {
        foreach (var observer in observers)
            observer.OnNext(time);
    }

    private void RemoveObserver(IObserver<float> observer)
    {
        lock (_lock)
            _observers.Remove(observer);
    }

    /// <summary>
    /// Runs world effects when the game time advances.
    /// </summary>
    /// <param name="newTime">New game time in hours.</param>
    /// <param name="oldTime">Old game time in hours.</param>
    public void OnTimeOfDayChange(float newTime, float oldTime)
    {
        // TODO: move time to WorldInstance
        if (oldTime > newTime)
            oldTime -= HoursPerDay;
        // Only check if it changed at least to the next 6 seconds
        if ((int)Math.Floor(newTime * 600f) == (int)Math.Floor(oldTime * 600f))
            return;

        var worlds = ActiveWorldManager?.GetWorlds();
        if (worlds == null)
            return;

        foreach (var world in worlds)
        {
            // check all active Npcs to check if their animation needs to be updated
            foreach (var npc in world.GetAllNpcs())
            {
                if (npc.Template.NpcPostureSets.Count <= 1)
                    continue;

                var oldAnim =
                    npc.Template.NpcPostureSets.FirstOrDefault(x => x.StartTodTime <= oldTime)?.AnimActionId ?? 0;
                var newAnim =
                    npc.Template.NpcPostureSets.FirstOrDefault(x => x.StartTodTime <= newTime)?.AnimActionId ?? 0;

                if (oldAnim != newAnim)
                    npc.BroadcastPacket(new SCUnitModelPostureChangedPacket(npc, newAnim, true), false);
            }

            // check all doodad of they have a ToD trigger in the current active group, and try to run it again
            foreach (var doodad in world.GetAllDoodads())
            {
                if (doodad.CurrentToDTriggers.Count <= 0)
                    continue;

                foreach (var (tod, nextPhase) in doodad.CurrentToDTriggers.ToArray())
                {
                    if (newTime >= tod && oldTime < tod)
                    {
                        if (nextPhase > 0)
                        {
                            try
                            {
                                var stablePhase = doodad.ResolveTodTransitionTarget((uint)nextPhase, tod);
                                doodad.ApplyTodPhase(null, (int)stablePhase);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(
                                    ex,
                                    "Time-of-day phase change failed for Doodad ObjId {0}, TemplateId {1}, next phase {2}",
                                    doodad.ObjId,
                                    doodad.TemplateId,
                                    nextPhase);
                            }

                            break;
                        }
                    }
                }
            }
        }
    }

    private readonly record struct ClockSample(
        float Hours,
        double SourceHours,
        double? SuppressEffectsUntilSourceHours);

    private sealed class TimeSubscription(TimeManager owner, IObserver<float> observer) : IDisposable
    {
        private TimeManager _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.RemoveObserver(observer);
        }
    }
}
