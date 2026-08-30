using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.TowerDefs;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

using NLog;

namespace AAEmu.Game.GameData;

[GameData]
public class TowerDefGameData : Singleton<TowerDefGameData>, IGameDataLoader
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<uint, TowerDef> _towerDefs = [];
    private readonly Dictionary<uint, TowerDefProg> _towerDefProgs = [];
    private readonly List<TowerDefValidationIssue> _validationIssues = [];

    public IReadOnlyDictionary<uint, TowerDef> TowerDefs => _towerDefs;
    public IReadOnlyList<TowerDefValidationIssue> ValidationIssues => _validationIssues;

    public TowerDef Get(uint id)
    {
        return _towerDefs.GetValueOrDefault(id);
    }

    public IReadOnlyCollection<TowerDef> GetAll()
    {
        return _towerDefs.Values;
    }

    public void Load(SqliteConnection connection)
    {
        _towerDefs.Clear();
        _towerDefProgs.Clear();
        _validationIssues.Clear();

        LoadTowerDefs(connection);
        LoadProgs(connection);
        LoadSpawnTargets(connection);
        LoadKillTargets(connection);
    }

    public void PostLoad()
    {
        foreach (var towerDef in _towerDefs.Values)
        {
            towerDef.Progs.Sort((left, right) => left.Id.CompareTo(right.Id));
            for (var i = 0; i < towerDef.Progs.Count; i++)
                towerDef.Progs[i].StepOrdinal = (uint)i;

            Validate(towerDef);
        }

        var errorCount = _validationIssues.Count(issue => issue.Severity == TowerDefValidationSeverity.Error);
        var validCount = _towerDefs.Values.Count(towerDef => towerDef.IsValid);
        Logger.Info(
            "Tower definitions loaded: {0} definitions, {1} valid, {2} validation issue(s), {3} error(s)",
            _towerDefs.Count,
            validCount,
            _validationIssues.Count,
            errorCount);

        foreach (var issue in _validationIssues)
        {
            if (issue.Severity == TowerDefValidationSeverity.Error)
                Logger.Error("TowerDef validation: {0}", issue.Message);
            else
                Logger.Warn("TowerDef validation: {0}", issue.Message);
        }
    }

    private void LoadTowerDefs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, name, start_msg, end_msg, tod, first_wave_after,
                                     target_npc_spawner_id, kill_npc_id, kill_npc_count,
                                     force_end_time, tod_day_interval, title_msg, milestone_id
                              FROM tower_defs
                              ORDER BY id
                              """;
        command.Prepare();
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var id = reader.GetUInt32("id");
            var towerDef = new TowerDef
            {
                Id = id,
                Name = reader.GetString("name", string.Empty),
                StartMsg = reader.GetString("start_msg", string.Empty),
                EndMsg = reader.GetString("end_msg", string.Empty),
                TimeOfDay = reader.GetFloat("tod"),
                FirstWaveAfter = reader.GetFloat("first_wave_after"),
                TargetNpcSpawnerId = GetNullableUInt32(reader, "target_npc_spawner_id"),
                KillNpcId = GetNullableUInt32(reader, "kill_npc_id"),
                KillNpcCount = GetNullableUInt32(reader, "kill_npc_count"),
                ForceEndTime = reader.GetFloat("force_end_time"),
                TimeOfDayDayInterval = reader.GetUInt32("tod_day_interval"),
                TitleMsg = reader.GetString("title_msg", string.Empty),
                MilestoneId = GetNullableUInt32(reader, "milestone_id")
            };

            if (!_towerDefs.TryAdd(id, towerDef))
                AddIssue(TowerDefValidationSeverity.Error, "tower_defs", id, id,
                    $"tower_defs contains duplicate id {id}");
        }
    }

    private void LoadProgs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, tower_def_id, msg, cond_to_next_time, cond_comp_by_and
                              FROM tower_def_progs
                              ORDER BY id
                              """;
        command.Prepare();
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var id = reader.GetUInt32("id");
            var towerDefId = reader.GetUInt32("tower_def_id");
            if (!_towerDefs.TryGetValue(towerDefId, out var towerDef))
            {
                AddIssue(TowerDefValidationSeverity.Warning, "tower_def_progs", id, towerDefId,
                    $"tower_def_progs id {id} references missing tower_def_id {towerDefId}; row skipped");
                continue;
            }

            var prog = new TowerDefProg
            {
                Id = id,
                TowerDef = towerDef,
                Msg = reader.GetString("msg", string.Empty),
                CondToNextTime = reader.GetFloat("cond_to_next_time"),
                CondCompByAnd = reader.GetBoolean("cond_comp_by_and", true)
            };

            if (!_towerDefProgs.TryAdd(id, prog))
            {
                AddIssue(TowerDefValidationSeverity.Error, "tower_def_progs", id, towerDefId,
                    $"tower_def_progs contains duplicate id {id}");
                towerDef.IsValid = false;
                continue;
            }

            towerDef.Progs.Add(prog);
        }
    }

    private void LoadSpawnTargets(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, tower_def_prog_id, spawn_target_id, spawn_target_type,
                                     despawn_on_next_step
                              FROM tower_def_prog_spawn_targets
                              ORDER BY id
                              """;
        command.Prepare();
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var id = reader.GetUInt32("id");
            var towerDefProgId = reader.GetUInt32("tower_def_prog_id");
            if (!_towerDefProgs.TryGetValue(towerDefProgId, out var towerDefProg))
            {
                AddIssue(TowerDefValidationSeverity.Warning, "tower_def_prog_spawn_targets", id, null,
                    $"tower_def_prog_spawn_targets id {id} references missing tower_def_prog_id {towerDefProgId}; row skipped");
                continue;
            }

            var rawType = reader.GetString("spawn_target_type", string.Empty);
            towerDefProg.SpawnTargets.Add(new TowerDefProgSpawnTarget
            {
                Id = id,
                SpawnTargetId = reader.GetUInt32("spawn_target_id"),
                SpawnTargetType = ParseTargetType(rawType),
                RawSpawnTargetType = rawType,
                DespawnOnNextStep = reader.GetBoolean("despawn_on_next_step", true),
                TowerDefProg = towerDefProg
            });
        }
    }

    private void LoadKillTargets(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, tower_def_prog_id, kill_target_id, kill_target_type, kill_count
                              FROM tower_def_prog_kill_targets
                              ORDER BY id
                              """;
        command.Prepare();
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        while (reader.Read())
        {
            var id = reader.GetUInt32("id");
            var towerDefProgId = reader.GetUInt32("tower_def_prog_id");
            if (!_towerDefProgs.TryGetValue(towerDefProgId, out var towerDefProg))
            {
                AddIssue(TowerDefValidationSeverity.Warning, "tower_def_prog_kill_targets", id, null,
                    $"tower_def_prog_kill_targets id {id} references missing tower_def_prog_id {towerDefProgId}; row skipped");
                continue;
            }

            var rawType = reader.GetString("kill_target_type", string.Empty);
            towerDefProg.KillTargets.Add(new TowerDefProgKillTarget
            {
                Id = id,
                KillTargetId = reader.GetUInt32("kill_target_id"),
                KillTargetType = ParseTargetType(rawType),
                RawKillTargetType = rawType,
                KillCount = reader.GetUInt32("kill_count"),
                TowerDefProg = towerDefProg
            });
        }
    }

    private void Validate(TowerDef towerDef)
    {
        if (!float.IsFinite(towerDef.TimeOfDay) || towerDef.TimeOfDay < 0f || towerDef.TimeOfDay >= 24f)
            Invalidate(towerDef, "tower_defs", towerDef.Id,
                $"tower_def {towerDef.Id} has invalid tod {towerDef.TimeOfDay}");
        if (!float.IsFinite(towerDef.FirstWaveAfter) || towerDef.FirstWaveAfter < 0f)
            Invalidate(towerDef, "tower_defs", towerDef.Id,
                $"tower_def {towerDef.Id} has invalid first_wave_after {towerDef.FirstWaveAfter}");
        if (!float.IsFinite(towerDef.ForceEndTime) || towerDef.ForceEndTime <= 0f)
            Invalidate(towerDef, "tower_defs", towerDef.Id,
                $"tower_def {towerDef.Id} has invalid force_end_time {towerDef.ForceEndTime}");
        if (towerDef.TimeOfDayDayInterval == 0)
            Invalidate(towerDef, "tower_defs", towerDef.Id,
                $"tower_def {towerDef.Id} has tod_day_interval 0");

        foreach (var prog in towerDef.Progs)
        {
            if (!float.IsFinite(prog.CondToNextTime) || prog.CondToNextTime < 0f)
                Invalidate(towerDef, "tower_def_progs", prog.Id,
                    $"tower_def_prog {prog.Id} has invalid cond_to_next_time {prog.CondToNextTime}");

            foreach (var target in prog.SpawnTargets)
            {
                if (target.SpawnTargetType is not (TowerDefTargetType.NpcSpawner or TowerDefTargetType.DoodadAlmighty))
                    Invalidate(towerDef, "tower_def_prog_spawn_targets", target.Id,
                        $"tower_def_prog_spawn_target {target.Id} has unsupported type '{target.RawSpawnTargetType}'");
            }

            foreach (var target in prog.KillTargets)
            {
                if (target.KillTargetType is not (TowerDefTargetType.Npc or TowerDefTargetType.DoodadAlmighty))
                    Invalidate(towerDef, "tower_def_prog_kill_targets", target.Id,
                        $"tower_def_prog_kill_target {target.Id} has unsupported type '{target.RawKillTargetType}'");
                if (target.KillCount == 0)
                    Invalidate(towerDef, "tower_def_prog_kill_targets", target.Id,
                        $"tower_def_prog_kill_target {target.Id} has kill_count 0");
            }

            if (prog.CondToNextTime <= 0f && prog.KillTargets.Count == 0)
                AddIssue(TowerDefValidationSeverity.Warning, "tower_def_progs", prog.Id, towerDef.Id,
                    $"tower_def_prog {prog.Id} has no timer or kill condition; runtime manifest must allow an immediate transition");
        }
    }

    private void Invalidate(TowerDef towerDef, string source, uint childId, string message)
    {
        towerDef.IsValid = false;
        AddIssue(TowerDefValidationSeverity.Error, source, childId, towerDef.Id, message);
    }

    private void AddIssue(TowerDefValidationSeverity severity, string source, uint childId, uint? towerDefId,
        string message)
    {
        _validationIssues.Add(new TowerDefValidationIssue(severity, source, childId, towerDefId, message));
    }

    private static uint? GetNullableUInt32(SQLiteWrapperReader reader, string column)
    {
        return reader.IsDBNull(column) ? null : reader.GetUInt32(column);
    }

    private static TowerDefTargetType ParseTargetType(string value)
    {
        return value switch
        {
            "NpcSpawner" => TowerDefTargetType.NpcSpawner,
            "Npc" => TowerDefTargetType.Npc,
            "DoodadAlmighty" => TowerDefTargetType.DoodadAlmighty,
            _ => TowerDefTargetType.Unknown
        };
    }
}
