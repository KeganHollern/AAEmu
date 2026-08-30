using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.TowerDefense;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NLog;
using GameTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.Game.Core.Managers.TowerDefense;

public sealed class TowerDefenseManager : Singleton<TowerDefenseManager>, ITowerDefenseManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly object _sync = new();
    private readonly ITimeManager _timeManager;
    private readonly IWorldManager _worldManager;
    private readonly IZoneManager _zoneManager;
    private readonly ITaskManager _taskManager;
    private readonly TimeProvider _timeProvider;
    private readonly TowerDefenseConfig _config;
    private readonly TowerDefenseOccurrenceRepository _repository = new();
    private readonly Dictionary<string, TowerDefenseEventManifest> _eventManifests =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TowerDefenseOccurrence> _occurrences =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenOccurrenceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _subscribedWorldIds = [];
    private IReadOnlyList<TowerDefenseOccurrenceRecord> _recoveryRecords = [];
    private bool _worldsReady;
    private bool _disposed;

    public static void SendSnapshotIfAvailable(Character character)
    {
        if (SingletonContainer.ServiceProvider?.GetService(typeof(ITowerDefenseManager)) is ITowerDefenseManager manager)
            manager.SendSnapshot(character);
    }

    public TowerDefenseManager(
        ITimeManager timeManager,
        IWorldManager worldManager,
        IZoneManager zoneManager,
        ITaskManager taskManager,
        TimeProvider timeProvider,
        IOptions<AppConfiguration> options)
    {
        _timeManager = timeManager;
        _worldManager = worldManager;
        _zoneManager = zoneManager;
        _taskManager = taskManager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _config = options?.Value.TowerDefense ?? new TowerDefenseConfig();
    }

    public void Load()
    {
        var manifestPath = Path.IsPathRooted(_config.ManifestPath)
            ? _config.ManifestPath
            : Path.Combine(FileManager.AppPath, _config.ManifestPath);
        var manifestFiles = Directory.Exists(manifestPath)
            ? Directory.GetFiles(manifestPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : File.Exists(manifestPath)
                ? [manifestPath]
                : throw new FileNotFoundException("Tower-defense manifest path was not found.", manifestPath);
        if (manifestFiles.Count == 0)
            throw new InvalidDataException($"Tower-defense manifest path contains no manifests: {manifestPath}");

        foreach (var file in manifestFiles)
        {
            var manifest = JsonConvert.DeserializeObject<TowerDefenseManifest>(File.ReadAllText(file))
                           ?? throw new InvalidDataException($"Tower-defense manifest is empty: {file}");
            ValidateManifest(manifest);
            foreach (var eventManifest in manifest.Events)
            {
                if (!_eventManifests.TryAdd(eventManifest.Key, eventManifest))
                    throw new InvalidDataException($"Tower-defense event key '{eventManifest.Key}' is duplicated across manifests.");
            }
        }

        _repository.EnsureSchema();
        _recoveryRecords = _repository.LoadRecoverable();

        Logger.Info(
            "Loaded {0} tower-defense manifests ({1} schedule-enabled; global enabled={2}, dryRun={3}).",
            _eventManifests.Count,
            _eventManifests.Values.Count(value => value.Enabled),
            _config.Enabled,
            _config.DryRun);
    }

    public void Initialize()
    {
        foreach (var manifest in _eventManifests.Values)
        {
            var definition = TowerDefGameData.Instance.Get(manifest.TowerDefId);
            if (definition == null)
                Logger.Error("Tower-defense event {0} references missing tower_def {1}.", manifest.Key, manifest.TowerDefId);
            else if (!definition.IsValid)
                Logger.Error("Tower-defense event {0} references invalid tower_def {1}; event is fail-closed.", manifest.Key, manifest.TowerDefId);
            else if (string.Equals(manifest.Trigger.Type, "TimeOfDay", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(manifest.Trigger.Hour - definition.TimeOfDay) > 0.001f ||
                    manifest.Trigger.DayInterval != definition.TimeOfDayDayInterval)
                {
                    Logger.Warn(
                        "Tower-defense event {0} trigger normalized to compact tower_def {1}: hour {2}, interval {3}.",
                        manifest.Key, definition.Id, definition.TimeOfDay, definition.TimeOfDayDayInterval);
                }
                manifest.Trigger.Hour = definition.TimeOfDay;
                manifest.Trigger.DayInterval = definition.TimeOfDayDayInterval;
                manifest.Trigger.DayPhase %= definition.TimeOfDayDayInterval;
            }
        }

        _timeManager.WorldClockChanged += OnWorldClockChanged;
    }

    public void OnWorldsInitialized()
    {
        lock (_sync)
        {
            _worldsReady = true;
            EnsureWorldSubscriptions();
            if (!_config.Enabled)
            {
                foreach (var record in _recoveryRecords)
                {
                    _seenOccurrenceKeys.Add(record.OccurrenceKey);
                    _repository.FinalizeRecord(record, TowerDefenseOccurrenceStatus.Cancelled.ToString(),
                        "runtime_disabled_after_restart");
                }
                _recoveryRecords = [];
                return;
            }
            RecoverPersistedOccurrences();

            var snapshot = _timeManager.GetSnapshot();
            foreach (var manifest in _eventManifests.Values.Where(value => value.Enabled))
            {
                if (!TowerDefenseSchedule.IsInsideCatchUpWindow(snapshot, manifest.Trigger, _timeManager.ClientSpeed))
                    continue;
                StartScheduled(manifest, snapshot.DayOrdinal, snapshot.ObservedAtUtc);
            }
        }
    }

    public IReadOnlyCollection<TowerDefenseOccurrence> GetActiveOccurrences()
    {
        lock (_sync)
            return _occurrences.Values.Where(IsActive).ToList();
    }

    public IReadOnlyList<string> GetEventDiagnostics()
    {
        lock (_sync)
        {
            return _eventManifests.Values
                .OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(value =>
                {
                    var definition = TowerDefGameData.Instance.Get(value.TowerDefId);
                    var validity = definition == null ? "missing compact" : definition.IsValid ? "valid" : "invalid compact";
                    return $"{value.Key} def={value.TowerDefId} schedule={(value.Enabled ? "enabled" : "disabled")} " +
                           $"sites={string.Join(',', value.Sites.Select(site => site.Key))} {validity}";
                })
                .ToList();
        }
    }

    public bool StartManual(string eventKeyOrTowerDefId, out string message) =>
        StartManual(eventKeyOrTowerDefId, null, out message);

    public bool StartManual(string eventKeyOrTowerDefId, string siteKey, out string message)
    {
        lock (_sync)
        {
            if (!_worldsReady)
            {
                message = "Tower-defense worlds are not initialized yet.";
                return false;
            }
            if (!_config.Enabled && !_config.AllowManualWhenDisabled)
            {
                message = "Tower-defense runtime is disabled by configuration.";
                return false;
            }
            if (!TryResolveManifest(eventKeyOrTowerDefId, out var manifest))
            {
                message = $"Unknown tower-defense event '{eventKeyOrTowerDefId}'.";
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            var key = $"{manifest.Key}:manual:{now.ToUnixTimeMilliseconds()}";
            return TryStart(manifest, key, _timeManager.GetSnapshot().DayOrdinal, now, siteKey, out message);
        }
    }

    public bool AdvanceManual(string eventKeyOrTowerDefId, out string message)
    {
        lock (_sync)
        {
            var occurrence = FindActive(eventKeyOrTowerDefId);
            if (occurrence == null)
            {
                message = $"No active occurrence matches '{eventKeyOrTowerDefId}'.";
                return false;
            }

            if (occurrence.Status == TowerDefenseOccurrenceStatus.FirstWaveDelay)
                EnterStep(occurrence, 0);
            else if (occurrence.Status == TowerDefenseOccurrenceStatus.StepActive)
                CompleteStep(occurrence);
            else
            {
                message = $"Occurrence is in {occurrence.Status} and cannot advance.";
                return false;
            }
            message = $"Advanced {occurrence.Manifest.Key}.";
            return true;
        }
    }

    public bool EndManual(string eventKeyOrTowerDefId, string reason, out string message)
    {
        lock (_sync)
        {
            var occurrence = FindActive(eventKeyOrTowerDefId);
            if (occurrence == null)
            {
                message = $"No active occurrence matches '{eventKeyOrTowerDefId}'.";
                return false;
            }
            EndOccurrence(occurrence, TowerDefenseOccurrenceStatus.Cancelled,
                string.IsNullOrWhiteSpace(reason) ? "gm_cancelled" : reason);
            message = $"Ended {occurrence.Manifest.Key}.";
            return true;
        }
    }

    public void SendSnapshot(Character character)
    {
        if (character == null)
            return;
        List<TowerDefInfo> infos;
        lock (_sync)
        {
            var characterGroup = _zoneManager.GetZoneByKey(character.Transform.ZoneId)?.GroupId ?? 0;
            infos = _occurrences.Values
                .Where(occurrence => IsActive(occurrence) &&
                                     ReferenceEquals(occurrence.World, character.ParentWorld) &&
                                     occurrence.ZoneGroupId == characterGroup)
                .Select(CreateInfo)
                .ToList();
        }
        character.SendPacket(new SCTowerDefListPacket(infos));
    }

    public void HandleTimer(string occurrenceKey, int expectedGeneration, TowerDefenseTimerKind kind)
    {
        lock (_sync)
        {
            if (!_occurrences.TryGetValue(occurrenceKey, out var occurrence) || !IsActive(occurrence))
                return;
            if (kind != TowerDefenseTimerKind.HardDeadline && occurrence.Generation != expectedGeneration)
                return;

            switch (kind)
            {
                case TowerDefenseTimerKind.FirstWave when occurrence.Status == TowerDefenseOccurrenceStatus.FirstWaveDelay:
                    EnterStep(occurrence, 0);
                    break;
                case TowerDefenseTimerKind.StepTimer when occurrence.Status == TowerDefenseOccurrenceStatus.StepActive:
                    occurrence.TimerCriterionComplete = true;
                    EvaluateStep(occurrence);
                    break;
                case TowerDefenseTimerKind.HardDeadline:
                    EndOccurrence(occurrence, TowerDefenseOccurrenceStatus.TimedOut, "hard_deadline");
                    break;
            }
        }
    }

    private void OnWorldClockChanged(WorldClockTick tick)
    {
        lock (_sync)
        {
            if (_disposed || !_worldsReady || !_config.Enabled)
                return;
            EnsureWorldSubscriptions();
            foreach (var manifest in _eventManifests.Values.Where(value => value.Enabled))
            {
                if (TowerDefenseSchedule.TryGetCrossedDay(tick, manifest.Trigger, out var dayOrdinal))
                    StartScheduled(manifest, dayOrdinal, tick.Current.ObservedAtUtc);
            }
        }
    }

    private void StartScheduled(TowerDefenseEventManifest manifest, long dayOrdinal, DateTimeOffset scheduledAt)
    {
        if (_config.DryRun)
        {
            Logger.Info("Tower-defense dry run: would start {0} for world day {1}.", manifest.Key, dayOrdinal);
            return;
        }
        var world = FindWorld(manifest.WorldTemplate);
        if (world == null)
        {
            Logger.Error("Tower-defense event {0}: world template {1} is not active.", manifest.Key, manifest.WorldTemplate);
            return;
        }
        var occurrenceKey = $"{manifest.Key}:{world.Id}:{dayOrdinal}";
        if (!TryStart(manifest, occurrenceKey, dayOrdinal, scheduledAt, null, out var message))
            Logger.Warn("Tower-defense schedule did not start {0}: {1}", manifest.Key, message);
    }

    private bool TryStart(
        TowerDefenseEventManifest manifest,
        string occurrenceKey,
        long dayOrdinal,
        DateTimeOffset scheduledAt,
        string requestedSiteKey,
        out string message)
    {
        if (_seenOccurrenceKeys.Contains(occurrenceKey) || _occurrences.ContainsKey(occurrenceKey) ||
            _repository.Contains(occurrenceKey))
        {
            message = "Occurrence was already handled.";
            return false;
        }
        if (_occurrences.Values.Any(value => IsActive(value) &&
            string.Equals(value.Manifest.ConcurrencyGroup, manifest.ConcurrencyGroup, StringComparison.OrdinalIgnoreCase)))
        {
            message = $"Concurrency group '{manifest.ConcurrencyGroup}' is already active.";
            return false;
        }

        var definition = TowerDefGameData.Instance.Get(manifest.TowerDefId);
        if (definition is not { IsValid: true })
        {
            message = $"tower_def {manifest.TowerDefId} is missing or invalid.";
            return false;
        }
        if (definition.Progs.Any(step => step.SpawnTargets.Any(target =>
                target.SpawnTargetType != TowerDefTargetType.NpcSpawner) ||
            step.KillTargets.Any(target => target.KillTargetType != TowerDefTargetType.Npc)))
        {
            message = $"tower_def {manifest.TowerDefId} uses an action or objective type not supported by the NPC tower-defense runtime.";
            return false;
        }
        if (!manifest.ImmediateTransitionAllowed && definition.Progs.Any(step =>
                step.CondToNextTime <= 0f && step.KillTargets.Count == 0))
        {
            message = $"tower_def {manifest.TowerDefId} has a step with no completion criterion.";
            return false;
        }
        var world = FindWorld(manifest.WorldTemplate);
        if (world?.SpawnManager == null)
        {
            message = $"World '{manifest.WorldTemplate}' is not ready.";
            return false;
        }

        var site = string.IsNullOrWhiteSpace(requestedSiteKey)
            ? SelectSite(manifest, occurrenceKey)
            : manifest.Sites.FirstOrDefault(value =>
                string.Equals(value.Key, requestedSiteKey, StringComparison.OrdinalIgnoreCase));
        if (site == null)
        {
            message = $"Unknown site '{requestedSiteKey}' for event '{manifest.Key}'.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(requestedSiteKey))
            Logger.Warn("Tower-defense GM site override: event={0}, occurrence={1}, site={2}.",
                manifest.Key, occurrenceKey, site.Key);
        if (!TryResolveAndPreflight(world, manifest, definition, site, out var eventZoneId, out var zoneGroupId, out message))
            return false;

        var now = _timeProvider.GetUtcNow();
        var occurrence = new TowerDefenseOccurrence
        {
            OccurrenceKey = occurrenceKey,
            Manifest = manifest,
            Definition = definition,
            Site = site,
            World = world,
            ScheduledDayOrdinal = dayOrdinal,
            ScheduledAtUtc = scheduledAt,
            StartedAtUtc = now,
            HardDeadlineUtc = now.AddSeconds(definition.ForceEndTime),
            Status = TowerDefenseOccurrenceStatus.Starting,
            Generation = 1,
            EventZoneId = eventZoneId,
            ZoneGroupId = zoneGroupId,
            DefinitionHash = ComputeDefinitionHash(manifest, definition)
        };
        if (definition.KillNpcId is > 0 && definition.KillNpcCount is > 0)
            occurrence.TerminalObjective = new TowerDefenseObjectiveProgress(
                definition.KillNpcId.Value, definition.KillNpcCount.Value, 0);
        _occurrences.Add(occurrenceKey, occurrence);
        _seenOccurrenceKeys.Add(occurrenceKey);
        _repository.Save(occurrence);

        try
        {
            if (site.Bindings.ContainsKey("initial"))
            {
                var initial = SpawnBinding(occurrence, "initial", "initial");
                occurrence.TargetObjId = initial.FirstOrDefault()?.ObjId ?? 0;
                if (occurrence.TargetObjId == 0)
                    throw new InvalidOperationException("Initial placement produced no NPC.");
            }

            occurrence.Announced = true;
            Broadcast(occurrence, new SCTowerDefStartPacket(CreateKey(occurrence), occurrence.EventZoneId));
            Schedule(occurrence, TowerDefenseTimerKind.HardDeadline,
                occurrence.HardDeadlineUtc - now, -1);

            if (definition.Progs.Count == 0)
            {
                occurrence.Status = TowerDefenseOccurrenceStatus.StepActive;
                occurrence.CurrentStepOrdinal = -1;
                _repository.Save(occurrence);
            }
            else if (definition.FirstWaveAfter > 0)
            {
                occurrence.Status = TowerDefenseOccurrenceStatus.FirstWaveDelay;
                _repository.Save(occurrence);
                Schedule(occurrence, TowerDefenseTimerKind.FirstWave,
                    TimeSpan.FromSeconds(definition.FirstWaveAfter), occurrence.Generation);
            }
            else
            {
                EnterStep(occurrence, 0);
            }

            message = $"Started {manifest.Key} at site {site.Key} (occurrence {occurrenceKey}).";
            Logger.Info(message);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Tower-defense event {0} failed during start.", manifest.Key);
            EndOccurrence(occurrence, TowerDefenseOccurrenceStatus.Failed, "start_failed");
            message = exception.Message;
            return false;
        }
    }

    private void EnterStep(TowerDefenseOccurrence occurrence, int stepOrdinal)
    {
        if (!IsActive(occurrence))
            return;
        if (stepOrdinal >= occurrence.Definition.Progs.Count)
        {
            EndOccurrence(occurrence, TowerDefenseOccurrenceStatus.Succeeded, "all_steps_complete");
            return;
        }

        CancelStepTasks(occurrence);
        CleanupPreviousStepLeases(occurrence);
        occurrence.Status = TowerDefenseOccurrenceStatus.StepTransition;
        occurrence.Generation++;
        occurrence.CurrentStepOrdinal = stepOrdinal;
        occurrence.StepEnteredAtUtc = _timeProvider.GetUtcNow();
        occurrence.TimerCriterionComplete = false;
        occurrence.CountedVictims.Clear();
        occurrence.Objectives.Clear();

        var step = occurrence.Definition.Progs[stepOrdinal];
        try
        {
            foreach (var spawnTarget in step.SpawnTargets)
            {
                if (spawnTarget.SpawnTargetType != TowerDefTargetType.NpcSpawner)
                    throw new NotSupportedException($"Spawn target type {spawnTarget.SpawnTargetType} is not implemented.");
                SpawnBinding(occurrence, spawnTarget.SpawnTargetId.ToString(CultureInfo.InvariantCulture),
                    $"step:{stepOrdinal}:target:{spawnTarget.Id}");
            }

            foreach (var killTarget in step.KillTargets)
            {
                if (killTarget.KillTargetType != TowerDefTargetType.Npc)
                    throw new NotSupportedException($"Kill target type {killTarget.KillTargetType} is not implemented.");
                occurrence.Objectives[killTarget.Id] =
                    new TowerDefenseObjectiveProgress(killTarget.KillTargetId, killTarget.KillCount, 0);
            }

            occurrence.Status = TowerDefenseOccurrenceStatus.StepActive;
            _repository.Save(occurrence);
            Broadcast(occurrence, new SCTowerDefWaveStartPacket(
                CreateKey(occurrence), occurrence.EventZoneId, ProtocolStep(stepOrdinal)));
            if (step.CondToNextTime > 0)
                Schedule(occurrence, TowerDefenseTimerKind.StepTimer,
                    TimeSpan.FromSeconds(step.CondToNextTime), occurrence.Generation);
            EvaluateStep(occurrence);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Tower-defense event {0} failed entering step {1}.", occurrence.Manifest.Key, stepOrdinal);
            EndOccurrence(occurrence, TowerDefenseOccurrenceStatus.Failed, "step_entry_failed");
        }
    }

    private void EvaluateStep(TowerDefenseOccurrence occurrence)
    {
        if (occurrence.Status != TowerDefenseOccurrenceStatus.StepActive || occurrence.CurrentStepOrdinal < 0)
            return;
        var step = occurrence.Definition.Progs[occurrence.CurrentStepOrdinal];
        var criteria = new List<bool>();
        if (step.CondToNextTime > 0)
            criteria.Add(occurrence.TimerCriterionComplete);
        criteria.AddRange(occurrence.Objectives.Values.Select(value => value.Current >= value.Required));
        if (criteria.Count == 0)
        {
            if (occurrence.Manifest.ImmediateTransitionAllowed)
                CompleteStep(occurrence);
            else
                EndOccurrence(occurrence, TowerDefenseOccurrenceStatus.Failed, "step_has_no_criteria");
            return;
        }
        var complete = step.CondCompByAnd ? criteria.All(value => value) : criteria.Any(value => value);
        if (complete)
            CompleteStep(occurrence);
    }

    private void CompleteStep(TowerDefenseOccurrence occurrence)
    {
        if (occurrence.Status != TowerDefenseOccurrenceStatus.StepActive &&
            occurrence.Status != TowerDefenseOccurrenceStatus.FirstWaveDelay)
            return;
        var next = occurrence.Status == TowerDefenseOccurrenceStatus.FirstWaveDelay
            ? 0
            : occurrence.CurrentStepOrdinal + 1;
        occurrence.Status = TowerDefenseOccurrenceStatus.StepTransition;
        EnterStep(occurrence, next);
    }

    private List<Npc> SpawnBinding(TowerDefenseOccurrence occurrence, string bindingKey, string actionKey)
    {
        if (!occurrence.Site.Bindings.TryGetValue(bindingKey, out var placementIds) || placementIds.Count == 0)
            throw new InvalidDataException($"Site {occurrence.Site.Key} has no binding '{bindingKey}'.");
        var spawned = new List<Npc>();
        foreach (var placementId in placementIds)
        {
            var spawner = occurrence.World.SpawnManager.GetEventPlacement(placementId)
                          ?? throw new InvalidDataException($"Event placement '{placementId}' was not loaded.");
            spawner.DespawnAll();
            spawner.Activate();
            var token = new TowerDefenseSpawnToken(
                occurrence.OccurrenceKey,
                occurrence.Manifest.Key,
                occurrence.Site.Key,
                occurrence.Generation,
                occurrence.CurrentStepOrdinal,
                actionKey + ":" + placementId);
            var npc = spawner.ForceSpawnOwned(token);
            spawner.Deactivate();
            if (npc == null)
                throw new InvalidOperationException($"Event placement '{placementId}' failed to spawn.");
            spawned.Add(npc);
        }
        return spawned;
    }

    private void OnUnitKilled(object sender, OnUnitKilledArgs args)
    {
        if (sender is not WorldInstance world || args.Victim is not Npc victim)
            return;
        lock (_sync)
        {
            if (!world.EventSpawnOwnership.TryGet(victim.ObjId, out var owned))
                return;
            foreach (var child in world.EventSpawnOwnership.GetChildren(victim.ObjId))
                DespawnOwned(world, child);
            if (_occurrences.TryGetValue(owned.Token.OccurrenceKey, out var terminalOccurrence) &&
                terminalOccurrence.TerminalObjective is { } terminalObjective &&
                terminalObjective.TargetId == victim.TemplateId &&
                terminalOccurrence.CountedTerminalVictims.Add(victim.ObjId))
            {
                terminalOccurrence.TerminalObjective = terminalObjective.Increment();
                if (terminalOccurrence.TerminalObjective.Current >= terminalOccurrence.TerminalObjective.Required)
                {
                    EndOccurrence(terminalOccurrence, TowerDefenseOccurrenceStatus.Succeeded,
                        "terminal_objective_complete");
                    return;
                }
            }
            if (!_occurrences.TryGetValue(owned.Token.OccurrenceKey, out var occurrence) ||
                occurrence.Status != TowerDefenseOccurrenceStatus.StepActive ||
                occurrence.Generation != owned.Token.Generation ||
                !occurrence.CountedVictims.Add(victim.ObjId))
                return;

            foreach (var (objectiveId, objective) in occurrence.Objectives.ToList())
            {
                if (objective.TargetId == victim.TemplateId && objective.Current < objective.Required)
                    occurrence.Objectives[objectiveId] = objective.Increment();
            }
            EvaluateStep(occurrence);
        }
    }

    private void EndOccurrence(
        TowerDefenseOccurrence occurrence,
        TowerDefenseOccurrenceStatus terminalStatus,
        string reason)
    {
        if (occurrence.Status is TowerDefenseOccurrenceStatus.Cleaning or TowerDefenseOccurrenceStatus.Ended)
            return;
        occurrence.Status = terminalStatus;
        occurrence.TerminalReason = reason;
        occurrence.Generation++;
        SaveBestEffort(occurrence);
        foreach (var task in occurrence.ScheduledTasks.ToList())
            _taskManager.Cancel(task);
        occurrence.ScheduledTasks.Clear();
        occurrence.Status = TowerDefenseOccurrenceStatus.Cleaning;

        foreach (var owned in occurrence.World.EventSpawnOwnership.GetOccurrence(occurrence.OccurrenceKey))
            DespawnOwned(occurrence.World, owned);
        foreach (var placementId in occurrence.Site.Bindings.Values.SelectMany(value => value).Distinct())
        {
            var placement = occurrence.World.SpawnManager.GetEventPlacement(placementId);
            placement?.Deactivate();
            placement?.DespawnAll();
        }
        if (occurrence.Announced)
            Broadcast(occurrence, new SCTowerDefEndPacket(CreateKey(occurrence), occurrence.EventZoneId));
        occurrence.Status = TowerDefenseOccurrenceStatus.Ended;
        SaveBestEffort(occurrence);
        _occurrences.Remove(occurrence.OccurrenceKey);
        Logger.Info("Tower-defense occurrence {0} ended: {1}.", occurrence.OccurrenceKey, reason);
    }

    private static void DespawnOwned(WorldInstance world, OwnedEventNpc owned)
    {
        try
        {
            owned.Npc.Spawner?.Despawn(owned.Npc);
        }
        finally
        {
            world.EventSpawnOwnership.Unregister(owned.Npc.ObjId);
        }
    }

    private void SuspendForShutdown(TowerDefenseOccurrence occurrence)
    {
        SaveBestEffort(occurrence);
        foreach (var task in occurrence.ScheduledTasks.ToList())
            _taskManager.Cancel(task);
        occurrence.ScheduledTasks.Clear();
        foreach (var owned in occurrence.World.EventSpawnOwnership.GetOccurrence(occurrence.OccurrenceKey))
            DespawnOwned(occurrence.World, owned);
        foreach (var placementId in occurrence.Site.Bindings.Values.SelectMany(value => value).Distinct())
        {
            var placement = occurrence.World.SpawnManager.GetEventPlacement(placementId);
            placement?.Deactivate();
            placement?.DespawnAll();
        }
        Logger.Info("Tower-defense occurrence {0} suspended for restart recovery.", occurrence.OccurrenceKey);
    }

    private void Schedule(
        TowerDefenseOccurrence occurrence,
        TowerDefenseTimerKind kind,
        TimeSpan delay,
        int expectedGeneration)
    {
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        var task = new TowerDefenseCallbackTask(this, occurrence.OccurrenceKey, expectedGeneration, kind);
        occurrence.ScheduledTasks.Add(task);
        _taskManager.Schedule(task, delay, count: 1);
    }

    private void CancelStepTasks(TowerDefenseOccurrence occurrence)
    {
        foreach (var task in occurrence.ScheduledTasks
                     .OfType<TowerDefenseCallbackTask>()
                     .Where(task => !task.Cancelled && task.Kind != TowerDefenseTimerKind.HardDeadline)
                     .ToList())
        {
            _taskManager.Cancel(task);
            task.Cancelled = true;
        }
        occurrence.ScheduledTasks.RemoveAll(task => task.Cancelled);
    }

    private static void CleanupPreviousStepLeases(TowerDefenseOccurrence occurrence)
    {
        if (occurrence.CurrentStepOrdinal < 0 ||
            occurrence.CurrentStepOrdinal >= occurrence.Definition.Progs.Count)
            return;
        var previousStep = occurrence.Definition.Progs[occurrence.CurrentStepOrdinal];
        var actionPrefixes = previousStep.SpawnTargets
            .Where(target => target.DespawnOnNextStep)
            .Select(target => $"step:{occurrence.CurrentStepOrdinal}:target:{target.Id}")
            .ToList();
        if (actionPrefixes.Count == 0)
            return;
        foreach (var owned in occurrence.World.EventSpawnOwnership.GetOccurrence(occurrence.OccurrenceKey))
        {
            if (actionPrefixes.Any(prefix => owned.Token.ActionKey.StartsWith(prefix, StringComparison.Ordinal)))
                DespawnOwned(occurrence.World, owned);
        }
    }

    private void EnsureWorldSubscriptions()
    {
        foreach (var world in _worldManager.GetWorlds())
        {
            if (!_subscribedWorldIds.Add(world.Id))
                continue;
            world.Events.OnUnitKilled += OnUnitKilled;
        }
    }

    private void RecoverPersistedOccurrences()
    {
        if (_recoveryRecords.Count == 0)
            return;
        var now = _timeProvider.GetUtcNow();
        foreach (var record in _recoveryRecords)
        {
            _seenOccurrenceKeys.Add(record.OccurrenceKey);
            if (record.HardDeadlineUtc <= now)
            {
                _repository.FinalizeRecord(record, TowerDefenseOccurrenceStatus.TimedOut.ToString(),
                    "expired_during_restart");
                continue;
            }
            if (!_eventManifests.TryGetValue(record.EventKey, out var manifest) ||
                TowerDefGameData.Instance.Get(record.TowerDefId) is not { IsValid: true } definition ||
                manifest.Sites.FirstOrDefault(site =>
                    string.Equals(site.Key, record.SiteKey, StringComparison.OrdinalIgnoreCase)) is not { } site)
            {
                _repository.FinalizeRecord(record, TowerDefenseOccurrenceStatus.Failed.ToString(),
                    "definition_or_site_missing_after_restart");
                continue;
            }
            var world = FindWorld(record.WorldTemplate);
            if (world == null || !TryResolveAndPreflight(world, manifest, definition, site,
                    out var eventZoneId, out var zoneGroupId, out _))
            {
                _repository.FinalizeRecord(record, TowerDefenseOccurrenceStatus.Failed.ToString(),
                    "world_or_placement_preflight_failed_after_restart");
                continue;
            }
            var currentHash = ComputeDefinitionHash(manifest, definition);
            if (!string.Equals(currentHash, record.DefinitionHash, StringComparison.OrdinalIgnoreCase))
            {
                _repository.FinalizeRecord(record, TowerDefenseOccurrenceStatus.Failed.ToString(),
                    "definition_hash_changed_after_restart");
                continue;
            }

            var occurrence = new TowerDefenseOccurrence
            {
                OccurrenceKey = record.OccurrenceKey,
                Manifest = manifest,
                Definition = definition,
                Site = site,
                World = world,
                ScheduledDayOrdinal = 0,
                ScheduledAtUtc = record.ScheduledAtUtc,
                StartedAtUtc = record.StartedAtUtc,
                StepEnteredAtUtc = record.StepEnteredAtUtc ?? default,
                HardDeadlineUtc = record.HardDeadlineUtc,
                Status = TowerDefenseOccurrenceStatus.Starting,
                Generation = Math.Max(1, record.Generation + 1),
                CurrentStepOrdinal = -1,
                EventZoneId = eventZoneId,
                ZoneGroupId = zoneGroupId,
                DefinitionHash = currentHash
            };
            if (definition.KillNpcId is > 0 && definition.KillNpcCount is > 0)
                occurrence.TerminalObjective = new TowerDefenseObjectiveProgress(
                    definition.KillNpcId.Value, definition.KillNpcCount.Value, 0);
            _occurrences.Add(occurrence.OccurrenceKey, occurrence);
            try
            {
                if (site.Bindings.ContainsKey("initial"))
                    occurrence.TargetObjId = SpawnBinding(occurrence, "initial", "recovery:initial")
                        .FirstOrDefault()?.ObjId ?? 0;
                occurrence.Announced = true;
                Broadcast(occurrence, new SCTowerDefStartPacket(CreateKey(occurrence), occurrence.EventZoneId));
                Schedule(occurrence, TowerDefenseTimerKind.HardDeadline,
                    occurrence.HardDeadlineUtc - now, -1);
                if (record.CurrentStep >= 0 && record.CurrentStep < definition.Progs.Count)
                    EnterStep(occurrence, record.CurrentStep);
                else if (definition.Progs.Count == 0)
                {
                    occurrence.Status = TowerDefenseOccurrenceStatus.StepActive;
                    _repository.Save(occurrence);
                }
                else
                {
                    occurrence.Status = TowerDefenseOccurrenceStatus.FirstWaveDelay;
                    _repository.Save(occurrence);
                    Schedule(occurrence, TowerDefenseTimerKind.FirstWave,
                        TimeSpan.FromSeconds(definition.FirstWaveAfter), occurrence.Generation);
                }
                Logger.Info("Recovered tower-defense occurrence {0} at step {1}.",
                    occurrence.OccurrenceKey, record.CurrentStep);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Failed to recover tower-defense occurrence {0}.", record.OccurrenceKey);
                EndOccurrence(occurrence, TowerDefenseOccurrenceStatus.Failed, "recovery_failed");
            }
        }
        _recoveryRecords = [];
    }

    private void SaveBestEffort(TowerDefenseOccurrence occurrence)
    {
        try
        {
            _repository.Save(occurrence);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to persist tower-defense occurrence {0} in state {1}.",
                occurrence.OccurrenceKey, occurrence.Status);
        }
    }

    private bool TryResolveAndPreflight(
        WorldInstance world,
        TowerDefenseEventManifest manifest,
        TowerDef definition,
        TowerDefenseSiteManifest site,
        out uint eventZoneId,
        out uint zoneGroupId,
        out string message)
    {
        eventZoneId = _worldManager.GetZoneId(world.Template, site.Anchor.X, site.Anchor.Y);
        var zone = _zoneManager.GetZoneByKey(eventZoneId);
        if (eventZoneId == 0 || zone == null)
        {
            zoneGroupId = 0;
            message = $"Anchor for site '{site.Key}' does not resolve to a zone.";
            return false;
        }
        zoneGroupId = zone.GroupId;
        if (manifest.ZoneGroupId != 0 && zoneGroupId != manifest.ZoneGroupId)
        {
            message = $"Site '{site.Key}' resolves to zone group {zoneGroupId}, expected {manifest.ZoneGroupId}.";
            return false;
        }
        site.EventZoneId = eventZoneId;

        var requiredBindings = definition.Progs
            .SelectMany(step => step.SpawnTargets)
            .Where(target => target.SpawnTargetType == TowerDefTargetType.NpcSpawner)
            .Select(target => target.SpawnTargetId.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in requiredBindings)
        {
            if (!site.Bindings.TryGetValue(key, out var ids) || ids.Count == 0)
            {
                message = $"Site '{site.Key}' is missing required binding '{key}'.";
                return false;
            }
        }
        foreach (var (binding, placementIds) in site.Bindings)
        {
            foreach (var placementId in placementIds)
            {
                var placement = world.SpawnManager.GetEventPlacement(placementId);
                if (placement == null)
                {
                    message = $"Binding '{binding}' references missing placement '{placementId}'.";
                    return false;
                }
                if (!string.Equals(placement.EventSiteKey, site.Key, StringComparison.OrdinalIgnoreCase))
                {
                    message = $"Placement '{placementId}' belongs to site '{placement.EventSiteKey}', not '{site.Key}'.";
                    return false;
                }
            }
        }
        message = null;
        return true;
    }

    private void Broadcast(TowerDefenseOccurrence occurrence, AAEmu.Game.Core.Network.Game.GamePacket packet)
    {
        foreach (var character in occurrence.World.GetAllCharacters())
        {
            var groupId = _zoneManager.GetZoneByKey(character.Transform.ZoneId)?.GroupId ?? 0;
            if (groupId == occurrence.ZoneGroupId)
                character.SendPacket(packet);
        }
    }

    private TowerDefInfo CreateInfo(TowerDefenseOccurrence occurrence) => new()
    {
        TowerDefKey = CreateKey(occurrence),
        ZoneId = occurrence.EventZoneId,
        SpotId = occurrence.Site.SpotId,
        TargetObjId = occurrence.TargetObjId,
        Position = new Point(occurrence.Site.Anchor.X, occurrence.Site.Anchor.Y, occurrence.Site.Anchor.Z),
        CurrentStep = ProtocolStep(occurrence.CurrentStepOrdinal)
    };

    private static TowerDefKey CreateKey(TowerDefenseOccurrence occurrence) => new()
    {
        TowerDefId = occurrence.Definition.Id,
        ZoneGroupId = checked((ushort)occurrence.ZoneGroupId)
    };

    private static uint ProtocolStep(int ordinal) => ordinal < 0 ? 0u : (uint)ordinal;

    private WorldInstance FindWorld(string templateName) =>
        _worldManager.GetWorlds().FirstOrDefault(world =>
            string.Equals(world.Template?.Name, templateName, StringComparison.OrdinalIgnoreCase));

    private static TowerDefenseSiteManifest SelectSite(TowerDefenseEventManifest manifest, string occurrenceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(occurrenceKey));
        var value = BitConverter.ToUInt32(bytes, 0);
        return manifest.Sites[(int)(value % manifest.Sites.Count)];
    }

    private bool TryResolveManifest(string keyOrId, out TowerDefenseEventManifest manifest)
    {
        if (_eventManifests.TryGetValue(keyOrId ?? string.Empty, out manifest))
            return true;
        return uint.TryParse(keyOrId, out var id) &&
               (manifest = _eventManifests.Values.FirstOrDefault(value => value.TowerDefId == id)) != null;
    }

    private TowerDefenseOccurrence FindActive(string keyOrId)
    {
        if (uint.TryParse(keyOrId, out var id))
            return _occurrences.Values.FirstOrDefault(value => IsActive(value) && value.Definition.Id == id);
        return _occurrences.Values.FirstOrDefault(value => IsActive(value) &&
            (string.Equals(value.Manifest.Key, keyOrId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value.OccurrenceKey, keyOrId, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsActive(TowerDefenseOccurrence occurrence) =>
        occurrence.Status is not (TowerDefenseOccurrenceStatus.Succeeded or
            TowerDefenseOccurrenceStatus.TimedOut or TowerDefenseOccurrenceStatus.Failed or
            TowerDefenseOccurrenceStatus.Cancelled or TowerDefenseOccurrenceStatus.Cleaning or
            TowerDefenseOccurrenceStatus.Ended);

    private static string ComputeDefinitionHash(TowerDefenseEventManifest manifest, TowerDef definition)
    {
        var payload = JsonConvert.SerializeObject(new
        {
            manifest.Key,
            manifest.TowerDefId,
            manifest.WorldTemplate,
            manifest.ZoneGroupId,
            manifest.ConcurrencyGroup,
            manifest.RestartPolicy,
            manifest.ImmediateTransitionAllowed,
            sites = manifest.Sites.Select(site => new
            {
                site.Key,
                site.SpotId,
                site.Anchor,
                bindings = site.Bindings
                    .OrderBy(binding => binding.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(binding => new
                    {
                        binding.Key,
                        Placements = binding.Value.Order(StringComparer.OrdinalIgnoreCase)
                    })
            }),
            definition.Id,
            definition.FirstWaveAfter,
            definition.ForceEndTime,
            definition.TargetNpcSpawnerId,
            definition.KillNpcId,
            definition.KillNpcCount,
            steps = definition.Progs.Select(step => new
            {
                step.Id,
                step.CondToNextTime,
                step.CondCompByAnd,
                spawns = step.SpawnTargets.Select(target => new { target.SpawnTargetId, target.SpawnTargetType }),
                kills = step.KillTargets.Select(target => new { target.KillTargetId, target.KillTargetType, target.KillCount })
            })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static void ValidateManifest(TowerDefenseManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported tower-defense schema version {manifest.SchemaVersion}.");
        var duplicateKey = manifest.Events.GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateKey != null)
            throw new InvalidDataException($"Tower-defense event key '{duplicateKey.Key}' is empty or duplicated.");
        foreach (var eventManifest in manifest.Events)
        {
            eventManifest.ConcurrencyGroup ??= eventManifest.Key;
            if (eventManifest.TowerDefId == 0 || string.IsNullOrWhiteSpace(eventManifest.WorldTemplate) ||
                eventManifest.Sites.Count == 0 || eventManifest.Trigger.DayInterval == 0 ||
                eventManifest.Trigger.DayPhase >= eventManifest.Trigger.DayInterval)
                throw new InvalidDataException($"Tower-defense event '{eventManifest.Key}' has invalid required fields.");
            var siteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var spotIds = new HashSet<uint>();
            foreach (var site in eventManifest.Sites)
            {
                if (string.IsNullOrWhiteSpace(site.Key) || !siteKeys.Add(site.Key) ||
                    site.SpotId == 0 || !spotIds.Add(site.SpotId))
                    throw new InvalidDataException($"Tower-defense event '{eventManifest.Key}' has invalid site keys or spot IDs.");
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timeManager.WorldClockChanged -= OnWorldClockChanged;
            foreach (var world in _worldManager.GetWorlds())
                world.Events.OnUnitKilled -= OnUnitKilled;
            foreach (var occurrence in _occurrences.Values.ToList())
                SuspendForShutdown(occurrence);
            _occurrences.Clear();
            _subscribedWorldIds.Clear();
        }
    }
}
