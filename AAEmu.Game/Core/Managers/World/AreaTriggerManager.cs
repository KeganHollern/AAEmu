using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Units;
using NLog;

namespace AAEmu.Game.Core.Managers.World;

public class AreaTriggerManager : Singleton<AreaTriggerManager>, IAreaTriggerManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly List<AreaTrigger> _areaTriggers = [];
    private List<AreaTrigger> _addQueue = [];
    private List<AreaTrigger> _removeQueue = [];

    private readonly object _addLock = new();
    private readonly object _remLock = new();

    public void Initialize()
    {
        TickManager.Instance.OnTick.Subscribe(Tick, TimeSpan.FromMilliseconds(200), true);
    }

    public void AddAreaTrigger(AreaTrigger trigger)
    {
        trigger.Owner?.AttachAreaTriggers.Add(trigger);
        lock (_addLock)
        {
            _addQueue.Add(trigger);
        }
    }

    public void RemoveAreaTrigger(AreaTrigger trigger)
    {
        trigger.OnDelete();
        lock (_remLock)
        {
            _removeQueue.Add(trigger);
        }
    }

    /// <summary>
    /// Immediately removes a unit from every trigger that still tracks it,
    /// firing the leave events (and inside-buff removal) without waiting for
    /// the next spatial diff. Call when a unit leaves the world.
    /// </summary>
    public void EvictUnit(Unit unit)
    {
        if (unit == null)
            return;

        AreaTrigger[] triggers;
        lock (_addLock)
        {
            triggers = [.. _areaTriggers, .. _addQueue];
        }

        foreach (var trigger in triggers)
            trigger?.ForceLeave(unit);
    }

    public void Tick(TimeSpan delta)
    {
        try
        {
            lock (_addLock)
            {
                if (_addQueue?.Count > 0)
                    _areaTriggers.AddRange(_addQueue);
                _addQueue = [];
            }

            foreach (var trigger in _areaTriggers)
            {
                // Tick triggers in player-active regions, and any trigger that
                // still tracks units inside: a unit that teleported away from a
                // now-quiet region must still get its leave event, otherwise
                // region-scoped area buffs (e.g. the Nui statue's "No Fight")
                // stick forever.
                if ((trigger?.Owner?.Region?.HasPlayerActivity() ?? false) || (trigger?.HasUnitsInside ?? false))
                    trigger?.Tick(delta);
            }

            lock (_remLock)
            {
                // _addLock guards _areaTriggers for concurrent EvictUnit snapshots.
                lock (_addLock)
                {
                    foreach (var triggerToRemove in _removeQueue)
                    {
                        _areaTriggers.Remove(triggerToRemove);
                    }
                }

                _removeQueue = [];
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error in AreaTrigger tick !");
        }
    }
}
