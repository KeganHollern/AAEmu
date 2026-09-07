using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;

using NLog;

namespace AAEmu.Game.Models.Game.World.Zones;

public class ZoneConflict
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly Action<ZoneConflictSnapshot> _broadcast;
    private ZoneConflictSnapshot _state = new(ZoneConflictType.Tension, 0, DateTime.MinValue);

    public ZoneConflict(ZoneGroup owner)
    {
        _clock = TimeProvider.System;
        _broadcast = state => WorldManager.Instance.BroadcastPacketToServer(
            new SCConflictZoneStatePacket(ZoneGroupId, state.State, state.NextStateTime));
    }

    internal ZoneConflict(TimeProvider clock, Action<ZoneConflictSnapshot> broadcast)
    {
        _clock = clock;
        _broadcast = broadcast;
    }

    public ushort ZoneGroupId { get; set; }
    public int[] NumKills { get; } = new int[5];
    public int[] NoKillMin { get; } = new int[5];
    public int ConflictMin { get; set; }
    public int WarMin { get; set; }
    public int PeaceMin { get; set; }
    public uint PeaceProtectedFactionId { get; set; }
    public uint NuiaReturnPointId { get; set; }
    public uint HariharaReturnPointId { get; set; }
    public uint WarTowerDefId { get; set; }
    public bool Closed { get; set; }

    public ZoneConflictType CurrentZoneState => GetSnapshot().State;
    public DateTime NextStateTime => GetSnapshot().NextStateTime;
    public uint KillCount => GetSnapshot().KillCount;
    private bool HasKillThresholds => NumKills.Any(value => value > 0);
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public ZoneConflictSnapshot GetSnapshot()
    {
        lock (_sync)
            return _state;
    }

    // Called after templates and persisted states have loaded, before players enter.
    public void Restore(ZoneConflictSnapshot? savedState)
    {
        lock (_sync)
        {
            if (Closed)
            {
                _state = new(ZoneConflictType.Tension, 0, DateTime.MinValue);
                return;
            }

            if (savedState.HasValue)
                _state = savedState.Value;
            else
                ChangeState(HasKillThresholds ? ZoneConflictType.Tension : ZoneConflictType.Conflict, UtcNow);

            AdvanceElapsedStates(UtcNow);
        }
    }

    /// <summary>Records a qualifying PvP kill during the cumulative escalation stages.</summary>
    public ZoneConflictType AddZoneKill(uint numberOfKills = 1)
    {
        lock (_sync)
        {
            var previous = _state.State;
            if (Closed || _state.State >= ZoneConflictType.Conflict || !HasKillThresholds)
                return previous;

            _state = _state with { KillCount = (uint)Math.Min(uint.MaxValue, (ulong)_state.KillCount + numberOfKills) };
            // Preserve the existing strict threshold comparison and cumulative counts.
            while (_state.State < ZoneConflictType.Conflict && _state.KillCount > NumKills[(int)_state.State])
            {
                var next = _state.State + 1;
                if (next == ZoneConflictType.Conflict)
                    ChangeState(next, UtcNow);
                else
                    _state = _state with { State = next };
            }
            if (_state.State != previous)
                BroadcastState();
            return previous;
        }
    }

    public void CheckTimer()
    {
        lock (_sync)
        {
            if (Closed)
                return;
            var previous = _state;
            AdvanceElapsedStates(UtcNow);
            if (_state != previous)
                BroadcastState();
        }
    }

    private void AdvanceElapsedStates(DateTime now)
    {
        // Timed-only zones and zones without a Peace phase repeat indefinitely.
        // Skip complete cycles during long outages, retaining the original UTC cadence.
        if (!HasKillThresholds || PeaceMin <= 0)
        {
            var cycleMinutes = (long)ConflictMin + WarMin + Math.Max(0, PeaceMin);
            if (_state.NextStateTime > DateTime.MinValue && now >= _state.NextStateTime && cycleMinutes > 0)
            {
                var cycleTicks = TimeSpan.FromMinutes(cycleMinutes).Ticks;
                var cycles = (now - _state.NextStateTime).Ticks / cycleTicks;
                _state = _state with { NextStateTime = _state.NextStateTime.AddTicks(cycles * cycleTicks) };
            }
        }

        // All shipped Conflict/War durations are positive. The bound also avoids
        // spinning forever if a future template contains an entirely zero-length cycle.
        for (var i = 0; i < 8 && _state.NextStateTime > DateTime.MinValue && now >= _state.NextStateTime; i++)
            ChangeState(NextState(), _state.NextStateTime);
    }

    public void SetState(ZoneConflictType state)
    {
        lock (_sync)
        {
            if (_state.State == state)
                return;
            ChangeState(state, UtcNow);
            BroadcastState();
        }
    }

    public void ForceNextState()
    {
        lock (_sync)
        {
            ChangeState(NextState(), UtcNow);
            BroadcastState();
        }
    }

    private ZoneConflictType NextState()
    {
        if (_state.State == ZoneConflictType.War && PeaceMin <= 0)
            return ZoneConflictType.Conflict;
        if (_state.State == ZoneConflictType.Peace)
            return HasKillThresholds ? ZoneConflictType.Tension : ZoneConflictType.Conflict;
        return _state.State + 1;
    }

    private void ChangeState(ZoneConflictType state, DateTime startedAt)
    {
        var deadline = state switch
        {
            ZoneConflictType.Conflict => startedAt.AddMinutes(ConflictMin),
            ZoneConflictType.War => startedAt.AddMinutes(WarMin),
            ZoneConflictType.Peace => startedAt.AddMinutes(PeaceMin),
            _ => DateTime.MinValue
        };
        _state = new(state, 0, deadline);
    }

    public void SendSwitchZoneState()
    {
        lock (_sync)
            BroadcastState();
    }

    private void BroadcastState()
    {
        try
        {
            _broadcast(_state);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to broadcast conflict state for ZoneGroup {ZoneGroupId}, State={_state.State}");
        }
    }
}
