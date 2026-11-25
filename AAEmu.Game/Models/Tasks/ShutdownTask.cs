using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using Microsoft.Extensions.Hosting;
using NLog;
using DotNetTask = System.Threading.Tasks.Task;

namespace AAEmu.Game.Models.Tasks;

public enum ShutdownScheduleResult
{
    Scheduled,
    Rescheduled,
    Cancelled,
    NoCountdown,
    SaveFailed,
    Stopping
}

public class ShutdownTask : Task
{
    private const int FinalNoticeMilliseconds = 3000;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IWorldManager _world;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Lock _state = new();
    private bool _alreadyShuttingDown;
    private DateTime _shutdownTime;
    private DateTime _nextTriggerTime = DateTime.MinValue;
    // Timings to show a popup, in seconds
    private readonly List<uint> _triggerPoints =
    [
        14400, // 4h
        10800, // 3h
        7200, // 2h
        3600, // 60m
        2700, // 45m
        1800, // 30m
        1200, // 20m
        900, // 15m
        600, // 10m
        300, // 5m
        240, // 4m
        180, // 3m
        120, // 2m
        90,
        60, // 1m
        45,
        30,
        20,
        10
    ];

    internal ShutdownTask(DateTime shutdownTime, IWorldManager world, IHostApplicationLifetime lifetime)
    {
        _shutdownTime = shutdownTime;
        _world = world;
        _lifetime = lifetime;
        _nextTriggerTime = CalculateLargestNextTrigger();
    }

    private DateTime CalculateLargestNextTrigger()
    {
        var remaining = _shutdownTime - DateTime.UtcNow;
        var remainingDelta = Math.Ceiling(remaining.TotalSeconds);
        foreach (var triggerPoint in _triggerPoints)
        {
            if (remainingDelta > triggerPoint)
                return _shutdownTime - TimeSpan.FromSeconds(triggerPoint);
        }

        return DateTime.MaxValue;
    }

    internal bool ChangeShutdownTime(DateTime shutdownTime)
    {
        lock (_state)
        {
            if (Cancelled || _alreadyShuttingDown)
                return false;
            _shutdownTime = shutdownTime;
            _nextTriggerTime = CalculateLargestNextTrigger();
            return true;
        }
    }

    internal bool CancelCountdown(ITaskManager scheduler)
    {
        lock (_state)
            return !_alreadyShuttingDown && scheduler.Cancel(this);
    }

    public override void Execute() => ExecuteAsync().GetAwaiter().GetResult();

    public override async DotNetTask ExecuteAsync()
    {
        lock (_state)
        {
            if (Cancelled || _alreadyShuttingDown)
                return;
            if (_shutdownTime > DateTime.UtcNow)
            {
                WarnBeforeShutdown();
                return;
            }
            _alreadyShuttingDown = true;
        }

        try
        {
            var characters = _world.GetAllCharacters();
            if (characters.Count != 0)
            {
                try
                {
                    // Type 1 displays immediately. Types 2 and 3 wait behind earlier countdown banners.
                    _world.BroadcastPacketToServer(new SCNoticeMessagePacket(1, Color.Magenta, FinalNoticeMilliseconds,
                        "The server is shutting down right now!"));
                    await DotNetTask.Delay(FinalNoticeMilliseconds, _lifetime.ApplicationStopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    // The host already received a separate stop request.
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, "Could not send the final shutdown notice.");
                }
                characters = _world.GetAllCharacters();
            }
            // The normal host disconnect does not clear the merchant buyback container.
            foreach (var character in characters)
            {
                try
                {
                    character.BuyBackItems?.Wipe();
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, "Could not clear buyback items for character {CharacterId}.", character.Id);
                }
            }
        }
        finally
        {
            // The host drains connections and scheduled work before its final database checkpoint.
            _lifetime.StopApplication();
        }
    }

    private void WarnBeforeShutdown()
    {
        var remainingTime = _shutdownTime - DateTime.UtcNow;
        var remainingSeconds = (int)Math.Ceiling(remainingTime.TotalSeconds);
        if (DateTime.UtcNow < _nextTriggerTime)
            return;

        _nextTriggerTime = CalculateLargestNextTrigger();

        var showSeconds = remainingSeconds <= 100;
        var showMinutes = remainingSeconds <= 3600 && !showSeconds;
        var showHours = remainingSeconds > 3600 && !showSeconds;

        var shutdownText = "The server is shutting down soon!";
        var popupTime = 3000;

        if (showHours)
        {
            shutdownText = $"The server is shutting down in {remainingSeconds / 3600} hours!";
            popupTime = 10000;
        }
        else if (showMinutes)
        {
            shutdownText = $"The server is shutting down in {remainingSeconds / 60} minutes!";
            popupTime = 6000;
        }
        else if (showSeconds)
        {
            shutdownText = $"The server is shutting down in {remainingSeconds} seconds!";
            popupTime = 3000;
        }

        _world.BroadcastPacketToServer(
            new SCNoticeMessagePacket(3,
                Color.Red,
                popupTime,
                shutdownText));
    }
}
