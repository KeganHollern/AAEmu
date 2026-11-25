using System.Diagnostics;

using AAEmu.Game.Services.Security;

using Prometheus;

namespace AAEmu.Game.Services.Health;

/// <summary>Process-wide measurements with one observation for each scheduler pass or save.</summary>
internal sealed class GameWorkMetrics
{
    internal static GameWorkMetrics Default => GameSecurityMetrics.Default.Work;

    private readonly Histogram.Child _activeRegion;
    private readonly Histogram.Child _npcAi;
    private readonly Histogram.Child _physics;
    private readonly Histogram.Child _databaseSave;
    private readonly Gauge _players;
    private readonly Gauge _activeSpawners;
    private readonly Counter _saveFailures;

    internal GameWorkMetrics(MetricFactory factory)
    {
        var duration = factory.CreateHistogram("aaemu_game_work_duration_seconds",
            "Elapsed work time per pass: all active regions, one NPC AI scheduler round, one world physics step excluding sleep, or a periodic or final database save.",
            new HistogramConfiguration
            {
                LabelNames = ["operation"],
                Buckets = [0.0001, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.04, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]
            });
        _activeRegion = duration.WithLabels("active_region");
        _npcAi = duration.WithLabels("npc_ai");
        _physics = duration.WithLabels("physics");
        _databaseSave = duration.WithLabels("database_save");
        _players = factory.CreateGauge("aaemu_game_players", "Characters in the most recent complete active-region pass across all worlds.");
        _activeSpawners = factory.CreateGauge("aaemu_game_active_spawners", "NPC spawners selected for update in the most recent complete active-region pass across all worlds.");
        _saveFailures = factory.CreateCounter("aaemu_game_database_save_failures_total", "Periodic or final database saves that did not complete.");
    }

    internal void RecordActiveRegion(TimeSpan duration, int players, int activeSpawners)
    {
        _players.Set(players);
        _activeSpawners.Set(activeSpawners);
        _activeRegion.Observe(duration.TotalSeconds);
    }

    internal WorkTimer MeasureNpcAi() => new(_npcAi);
    internal WorkTimer MeasurePhysics() => new(_physics);

    internal void RecordDatabaseSave(TimeSpan duration, bool saved)
    {
        _databaseSave.Observe(duration.TotalSeconds);
        if (!saved)
            _saveFailures.Inc();
    }

    // Keep the common AI and physics paths free of timer allocations and label lookups.
    internal readonly struct WorkTimer(Histogram.Child duration) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose() => duration.Observe(Stopwatch.GetElapsedTime(_start).TotalSeconds);
    }
}
