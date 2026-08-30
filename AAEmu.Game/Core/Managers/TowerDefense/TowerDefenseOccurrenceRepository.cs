using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.TowerDefs;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;

namespace AAEmu.Game.Core.Managers.TowerDefense;

internal sealed class TowerDefenseOccurrenceRepository
{
    public void EnsureSchema()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TowerDefenseOccurrenceRecord> LoadRecoverable()
    {
        var records = new List<TowerDefenseOccurrenceRecord>();
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT occurrence_key, event_key, tower_def_id, world_template, world_instance_id,
                   zone_group_id, site_key, status, state_generation, current_step,
                   scheduled_at, started_at, step_entered_at, hard_deadline, definition_hash,
                   objective_progress, terminal_reason
              FROM tower_def_occurrences
             WHERE status NOT IN ('Ended', 'Succeeded', 'TimedOut', 'Failed', 'Cancelled')
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new TowerDefenseOccurrenceRecord
            {
                OccurrenceKey = reader.GetString("occurrence_key"),
                EventKey = reader.GetString("event_key"),
                TowerDefId = reader.GetUInt32("tower_def_id"),
                WorldTemplate = reader.GetString("world_template"),
                WorldInstanceId = reader.GetUInt32("world_instance_id"),
                ZoneGroupId = reader.GetUInt32("zone_group_id"),
                SiteKey = reader.GetString("site_key"),
                Status = Enum.TryParse<TowerDefenseOccurrenceStatus>(reader.GetString("status"), out var status)
                    ? status
                    : TowerDefenseOccurrenceStatus.Failed,
                Generation = reader.GetInt32("state_generation"),
                CurrentStep = reader.GetInt32("current_step"),
                ScheduledAtUtc = DateTime.SpecifyKind(reader.GetDateTime("scheduled_at"), DateTimeKind.Utc),
                StartedAtUtc = DateTime.SpecifyKind(reader.GetDateTime("started_at"), DateTimeKind.Utc),
                StepEnteredAtUtc = reader.IsDBNull(reader.GetOrdinal("step_entered_at"))
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime("step_entered_at"), DateTimeKind.Utc),
                HardDeadlineUtc = DateTime.SpecifyKind(reader.GetDateTime("hard_deadline"), DateTimeKind.Utc),
                DefinitionHash = reader.GetString("definition_hash"),
                ObjectiveProgress = reader.IsDBNull(reader.GetOrdinal("objective_progress"))
                    ? "{}"
                    : reader.GetString("objective_progress"),
                TerminalReason = reader.IsDBNull(reader.GetOrdinal("terminal_reason"))
                    ? null
                    : reader.GetString("terminal_reason")
            });
        }
        return records;
    }

    public bool Contains(string occurrenceKey)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM tower_def_occurrences WHERE occurrence_key = @occurrence_key LIMIT 1";
        Add(command, "@occurrence_key", occurrenceKey);
        return command.ExecuteScalar() != null;
    }

    public void Save(TowerDefenseOccurrence occurrence)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tower_def_occurrences
                (occurrence_key, event_key, tower_def_id, world_template, world_instance_id,
                 zone_group_id, site_key, status, state_generation, current_step,
                 scheduled_at, started_at, step_entered_at, hard_deadline, definition_hash,
                 objective_progress, terminal_reason, updated_at)
            VALUES
                (@occurrence_key, @event_key, @tower_def_id, @world_template, @world_instance_id,
                 @zone_group_id, @site_key, @status, @state_generation, @current_step,
                 @scheduled_at, @started_at, @step_entered_at, @hard_deadline, @definition_hash,
                 @objective_progress, @terminal_reason, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                status = VALUES(status),
                state_generation = VALUES(state_generation),
                current_step = VALUES(current_step),
                step_entered_at = VALUES(step_entered_at),
                hard_deadline = VALUES(hard_deadline),
                definition_hash = VALUES(definition_hash),
                objective_progress = VALUES(objective_progress),
                terminal_reason = VALUES(terminal_reason),
                updated_at = UTC_TIMESTAMP()
            """;
        Add(command, "@occurrence_key", occurrence.OccurrenceKey);
        Add(command, "@event_key", occurrence.Manifest.Key);
        Add(command, "@tower_def_id", occurrence.Definition.Id);
        Add(command, "@world_template", occurrence.Manifest.WorldTemplate);
        Add(command, "@world_instance_id", occurrence.World.Id);
        Add(command, "@zone_group_id", occurrence.ZoneGroupId);
        Add(command, "@site_key", occurrence.Site.Key);
        Add(command, "@status", occurrence.Status.ToString());
        Add(command, "@state_generation", occurrence.Generation);
        Add(command, "@current_step", occurrence.CurrentStepOrdinal);
        Add(command, "@scheduled_at", occurrence.ScheduledAtUtc.UtcDateTime);
        Add(command, "@started_at", occurrence.StartedAtUtc.UtcDateTime);
        Add(command, "@step_entered_at", occurrence.StepEnteredAtUtc == default
            ? DBNull.Value
            : occurrence.StepEnteredAtUtc.UtcDateTime);
        Add(command, "@hard_deadline", occurrence.HardDeadlineUtc.UtcDateTime);
        Add(command, "@definition_hash", occurrence.DefinitionHash);
        Add(command, "@objective_progress", JsonConvert.SerializeObject(occurrence.Objectives));
        Add(command, "@terminal_reason", (object)occurrence.TerminalReason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void FinalizeRecord(TowerDefenseOccurrenceRecord record, string status, string reason)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tower_def_occurrences
               SET status = @status, terminal_reason = @reason, updated_at = UTC_TIMESTAMP()
             WHERE occurrence_key = @occurrence_key
            """;
        Add(command, "@status", status);
        Add(command, "@reason", reason);
        Add(command, "@occurrence_key", record.OccurrenceKey);
        command.ExecuteNonQuery();
    }

    private static void Add(MySqlCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS `tower_def_occurrences` (
            `occurrence_key` VARCHAR(191) NOT NULL,
            `event_key` VARCHAR(128) NOT NULL,
            `tower_def_id` INT UNSIGNED NOT NULL,
            `world_template` VARCHAR(64) NOT NULL,
            `world_instance_id` INT UNSIGNED NOT NULL,
            `zone_group_id` INT UNSIGNED NOT NULL,
            `site_key` VARCHAR(128) NOT NULL,
            `status` VARCHAR(32) NOT NULL,
            `state_generation` INT NOT NULL DEFAULT 0,
            `current_step` INT NOT NULL DEFAULT -1,
            `scheduled_at` DATETIME(6) NOT NULL,
            `started_at` DATETIME(6) NOT NULL,
            `step_entered_at` DATETIME(6) NULL,
            `hard_deadline` DATETIME(6) NOT NULL,
            `definition_hash` CHAR(64) NOT NULL,
            `objective_progress` LONGTEXT NULL,
            `terminal_reason` VARCHAR(255) NULL,
            `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
            PRIMARY KEY (`occurrence_key`),
            INDEX `idx_tower_def_occurrences_active` (`status`, `world_instance_id`),
            INDEX `idx_tower_def_occurrences_event` (`event_key`, `scheduled_at`)
        ) ENGINE=InnoDB DEFAULT COLLATE='utf8mb4_general_ci'
        """;
}

internal sealed class TowerDefenseOccurrenceRecord
{
    public string OccurrenceKey { get; init; }
    public string EventKey { get; init; }
    public uint TowerDefId { get; init; }
    public string WorldTemplate { get; init; }
    public uint WorldInstanceId { get; init; }
    public uint ZoneGroupId { get; init; }
    public string SiteKey { get; init; }
    public TowerDefenseOccurrenceStatus Status { get; init; }
    public int Generation { get; init; }
    public int CurrentStep { get; init; }
    public DateTimeOffset ScheduledAtUtc { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? StepEnteredAtUtc { get; init; }
    public DateTimeOffset HardDeadlineUtc { get; init; }
    public string DefinitionHash { get; init; }
    public string ObjectiveProgress { get; init; }
    public string TerminalReason { get; init; }
}
