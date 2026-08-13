using System.Globalization;
using System.Text;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class CatalogRecordService
{
    public static readonly IReadOnlyDictionary<uint, (string Name, string Description)> Abilities =
        new Dictionary<uint, (string Name, string Description)>
        {
            [1] = ("Battlerage", "Close-range attacks, mobility, and weapon-focused combat."),
            [2] = ("Witchcraft", "Crowd control, debuffs, fear, sleep, and disruption."),
            [3] = ("Defense", "Shields, protection, survivability, and enemy control."),
            [4] = ("Auramancy", "Mobility, magical protection, recovery, and support."),
            [5] = ("Occultism", "Magic that drains, impales, summons, and weakens enemies."),
            [6] = ("Archery", "Ranged bow attacks, kiting, and sustained physical damage."),
            [7] = ("Sorcery", "Elemental ranged magic and high burst damage."),
            [8] = ("Shadowplay", "Stealth, mobility, poisons, and quick attacks."),
            [9] = ("Songcraft", "Songs that strengthen allies and hinder enemies."),
            [10] = ("Vitalism", "Healing, resurrection, and protective support.")
        };

    private static readonly HashSet<string> s_booleanNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "show", "visible", "enabled", "friendly", "non_friendly", "auto_learn", "need_learn",
        "target_alive", "target_dead", "source_alive", "source_dead", "default_gcd", "ignore_global_cooldown",
        "casting_cancelable", "casting_delayable", "channeling_cancelable", "use_anim_time", "keep_stealth",
        "target_siege", "target_water", "target_only_water", "source_mount", "source_mount_mate", "unmount"
    };

    private static readonly Dictionary<string, string> s_help = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "The number the client and server use to identify this entry.",
        ["name"] = "The internal fallback name. Player-facing text normally comes from Translations.",
        ["title"] = "The internal fallback title. Player-facing text normally comes from Translations.",
        ["desc"] = "The internal fallback description.",
        ["web_desc"] = "The detailed description shown in a tooltip or skill information panel.",
        ["ability_id"] = "The combat skillset this skill belongs to, such as Battlerage or Sorcery.",
        ["ability_level"] = "The skillset level required before this skill becomes available.",
        ["mana_cost"] = "Base mana consumed when the skill is used.",
        ["cooldown_time"] = "How long the player must wait before using it again, in milliseconds.",
        ["casting_time"] = "How long casting takes, in milliseconds. Zero means immediate.",
        ["min_range"] = "Minimum usable distance, in meters.",
        ["max_range"] = "Maximum usable distance, in meters.",
        ["target_area_radius"] = "Radius affected around the target, in meters.",
        ["target_area_count"] = "Maximum number of targets in the affected area.",
        ["consume_lp"] = "Labor points consumed when this action succeeds.",
        ["show"] = "Whether this entry is intended to be visible to players.",
        ["icon_id"] = "References an existing client icon. Adding a new icon requires client asset work.",
        ["model"] = "References an existing client model or visual asset.",
        ["skill_id"] = "Links this entry to a skill.",
        ["item_id"] = "Links this entry to an item.",
        ["effect_id"] = "Links this skill step to an effect definition.",
        ["duration"] = "Base duration in milliseconds. Some buffs instead last until their shield charge is depleted or another removal rule fires.",
        ["level_duration"] = "Additional duration in milliseconds for each applicable ability level.",
        ["init_min_charge"] = "Minimum starting charge. For shield buffs, this is the minimum damage the protection can absorb.",
        ["init_max_charge"] = "Maximum starting charge. For shield buffs, this is the maximum damage the protection can absorb.",
        ["max_stack"] = "Maximum number of copies of this buff that can be active together.",
        ["damage_absorption_per_hit"] = "Maximum damage absorbed from one hit. Zero normally means no per-hit cap.",
        ["chance"] = "Percentage chance that this effect is applied.",
        ["stack"] = "Number of buff stacks applied at once.",
        ["unit_attribute_id"] = "The character statistic changed by this modifier. The gameplay summary translates known IDs into names.",
        ["unit_modifier_type_id"] = "Zero adds a flat amount; one applies a percentage.",
        ["value"] = "Base amount added to the selected character statistic.",
        ["linear_level_bonus"] = "Extra amount added for each applicable level.",
        ["req_doodad_id"] = "The workbench or world object required to perform this action. Zero means none."
    };

    public CatalogRecord? GetRecord(string compactPath, string table, uint id, string language = "en_us")
    {
        CompactCatalogService.ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        var columns = ReadColumns(connection, table);
        if (columns.Count == 0 || columns.All(column => !column.Name.Equals("id", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {BaselineVerifier.QuoteIdentifier(table)} WHERE id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var fields = new List<CatalogRecordField>(reader.FieldCount);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var name = reader.GetName(index);
            var type = columns.First(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Type;
            var rawValue = reader.IsDBNull(index) ? null : reader.GetValue(index);
            var isNull = IsCompactNull(rawValue);
            var value = isNull ? null : FormatValue(rawValue!);
            fields.Add(new CatalogRecordField
            {
                Name = name,
                Label = FriendlyName(name),
                Type = type,
                Group = ClassifyGroup(table, name),
                Help = Describe(name),
                Value = value,
                IsNull = isNull,
                IsBoolean = IsBoolean(name, type, value),
                IsEssential = IsEssential(table, name),
                IsIdentity = name.Equals("id", StringComparison.OrdinalIgnoreCase),
                IsEditable = !type.Contains("BLOB", StringComparison.OrdinalIgnoreCase),
                ReferenceTable = ReferenceTableFor(name)
            });
        }
        reader.Close();

        var localizations = ReadLocalizations(connection, table, id);
        var nameValue = localizations
            .Where(field => field.Field is "name" or "title")
            .Select(field => field.Values.GetValueOrDefault(language))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        nameValue ??= fields.FirstOrDefault(field => field.Name is "name" or "title")?.Value;

        var (kind, kindLabel) = ClassifyTable(table);
        return new CatalogRecord
        {
            Table = table,
            Id = id,
            Name = string.IsNullOrWhiteSpace(nameValue) ? $"{kindLabel} {id}" : nameValue,
            Kind = kind,
            KindLabel = kindLabel,
            CanChange = true,
            CanDuplicate = true,
            DuplicateNote = DuplicateNote(table),
            Fields = fields,
            Localizations = localizations,
            RelatedSections = ReadRelatedSections(connection, table, id),
            GameplayLinks = table.Equals("skills", StringComparison.OrdinalIgnoreCase) ? ReadSkillGameplayLinks(connection, id, language) : []
        };
    }

    public AbilityGraph? GetAbility(string compactPath, uint abilityId, string language = "en_us")
    {
        CompactCatalogService.ValidateLanguageColumn(language);
        if (!Abilities.TryGetValue(abilityId, out var ability))
        {
            return null;
        }

        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id,
                   COALESCE(NULLIF(n.{BaselineVerifier.QuoteIdentifier(language)}, ''), s.name, ''),
                   COALESCE(NULLIF(w.{BaselineVerifier.QuoteIdentifier(language)}, ''), NULLIF(d.{BaselineVerifier.QuoteIdentifier(language)}, ''), s.web_desc, s.desc, ''),
                   COALESCE(s.ability_level, 0), COALESCE(s.mana_cost, 0), COALESCE(s.casting_time, 0),
                   COALESCE(s.cooldown_time, 0), COALESCE(s.show, 0)
              FROM skills s
              LEFT JOIN localized_texts n ON n.tbl_name = 'skills' AND n.tbl_column_name = 'name' AND n.idx = s.id
              LEFT JOIN localized_texts w ON w.tbl_name = 'skills' AND w.tbl_column_name = 'web_desc' AND w.idx = s.id
              LEFT JOIN localized_texts d ON d.tbl_name = 'skills' AND d.tbl_column_name = 'desc' AND d.idx = s.id
             WHERE s.ability_id = @ability
             ORDER BY COALESCE(s.show, 0) DESC, COALESCE(s.ability_level, 0), s.id;
            """;
        command.Parameters.AddWithValue("@ability", abilityId);
        using var reader = command.ExecuteReader();
        var skills = new List<AbilitySkillSummary>();
        while (reader.Read())
        {
            skills.Add(new AbilitySkillSummary
            {
                Id = Convert.ToUInt32(reader.GetInt64(0)),
                Name = reader.GetString(1),
                Description = reader.GetString(2),
                RequiredAbilityLevel = reader.GetInt32(3),
                ManaCost = reader.GetInt32(4),
                CastTime = reader.GetInt32(5),
                Cooldown = reader.GetInt32(6),
                Visible = ReadBoolean(reader.GetValue(7))
            });
        }

        return new AbilityGraph { Id = abilityId, Name = ability.Name, Description = ability.Description, Skills = skills };
    }

    private static List<(string Name, string Type)> ReadColumns(SqliteConnection connection, string table)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
        exists.Parameters.AddWithValue("@name", table);
        if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({BaselineVerifier.QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<(string Name, string Type)>();
        while (reader.Read())
        {
            columns.Add((reader.GetString(1), reader.GetString(2)));
        }
        return columns;
    }

    private static List<CatalogLocalizationField> ReadLocalizations(SqliteConnection connection, string table, uint id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tbl_column_name, ko, en_us, zh_cn, ja, ru, zh_tw, de, fr
              FROM localized_texts
             WHERE tbl_name = @table AND idx = @id
             ORDER BY tbl_column_name, id;
            """;
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        var result = new List<CatalogLocalizationField>();
        while (reader.Read())
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = reader.IsDBNull(index) ? string.Empty : reader.GetString(index);
            }
            result.Add(new CatalogLocalizationField { Field = reader.GetString(0), Label = FriendlyName(reader.GetString(0)), Values = values });
        }
        return result;
    }

    private static List<CatalogRelatedSection> ReadRelatedSections(SqliteConnection connection, string table, uint id)
    {
        List<(string Table, string Owner, string Title, string Description, string Extra)> definitions = table.ToLowerInvariant() switch
        {
            "skills" =>
            [
                ("skill_effects", "skill_id", "Effect links", "Rules that decide which gameplay effect runs at each skillset level.", string.Empty),
                ("skill_reagents", "skill_id", "Items consumed", "Items the skill consumes when used.", string.Empty),
                ("skill_products", "skill_id", "Items produced", "Items the skill creates when it succeeds.", string.Empty),
                ("tagged_skills", "skill_id", "Skill tags", "Tags used by combos, restrictions, and other game systems to recognize this skill.", string.Empty),
                ("tooltip_skill_effects", "skill_id", "Tooltip effects", "Effect references used when the client explains the skill.", string.Empty),
                ("unit_reqs", "owner_id", "Use requirements", "Conditions that must be true before this skill can be used.", " AND owner_type = 'Skill'")
            ],
            "buffs" =>
            [
                ("unit_modifiers", "owner_id", "Attribute changes", "Direct changes to armor, movement, damage, detection, and other character attributes.", " AND owner_type = 'Buff'"),
                ("dynamic_unit_modifiers", "buff_id", "Changing attribute effects", "Attribute changes whose strength follows a configured function.", string.Empty),
                ("buff_tick_effects", "buff_id", "Repeated effects", "Effects that run every buff tick, such as periodic damage or healing.", string.Empty),
                ("tagged_buffs", "buff_id", "Buff tags", "Tags used by immunity, combos, and removal rules to recognize this buff.", string.Empty),
                ("buff_unit_modifiers", "owner_id", "Buff links", "Other buffs enabled or modified by this buff.", " AND owner_type = 'Buff'")
            ],
            _ => []
        };

        var sections = new List<CatalogRelatedSection>();
        foreach (var definition in definitions)
        {
            var columns = ReadColumns(connection, definition.Table);
            if (columns.Count == 0) continue;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {BaselineVerifier.QuoteIdentifier(definition.Table)} WHERE {BaselineVerifier.QuoteIdentifier(definition.Owner)} = @id{definition.Extra} ORDER BY id;";
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            var rows = new List<CatalogRelatedRow>();
            while (reader.Read())
            {
                var fields = new List<CatalogRecordField>(reader.FieldCount);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var name = reader.GetName(index);
                    var type = columns.First(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Type;
                    var rawValue = reader.IsDBNull(index) ? null : reader.GetValue(index);
                    var isNull = IsCompactNull(rawValue);
                    var value = isNull ? null : FormatValue(rawValue!);
                    fields.Add(new CatalogRecordField
                    {
                        Name = name,
                        Label = FriendlyName(name),
                        Type = type,
                        Group = ClassifyGroup(definition.Table, name),
                        Help = Describe(name),
                        Value = value,
                        IsNull = isNull,
                        IsBoolean = IsBoolean(name, type, value),
                        IsEssential = true,
                        IsIdentity = name.Equals("id", StringComparison.OrdinalIgnoreCase) || name.Equals(definition.Owner, StringComparison.OrdinalIgnoreCase),
                        IsEditable = !type.Contains("BLOB", StringComparison.OrdinalIgnoreCase),
                        ReferenceTable = ReferenceTableFor(name)
                    });
                }
                var rowId = Convert.ToUInt32(reader["id"], CultureInfo.InvariantCulture);
                rows.Add(new CatalogRelatedRow { Id = rowId, Label = BuildRelatedRowLabel(definition.Table, rowId, fields), Fields = fields });
            }
            if (rows.Count > 0)
                sections.Add(new CatalogRelatedSection { Table = definition.Table, OwnerColumn = definition.Owner, Title = definition.Title, Description = definition.Description, Rows = rows });
        }
        return sections;
    }

    private static string BuildRelatedRowLabel(string table, uint rowId, IReadOnlyList<CatalogRecordField> fields)
    {
        string? Value(string name) => fields.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        if (table.Equals("unit_modifiers", StringComparison.OrdinalIgnoreCase) && int.TryParse(Value("unit_attribute_id"), out var attribute))
        {
            var suffix = Value("unit_modifier_type_id") == "1" ? "%" : string.Empty;
            var amount = int.TryParse(Value("value"), out var numeric) ? $"{numeric:+#;-#;0}{suffix}" : Value("value");
            return $"{FriendlyAttribute(attribute)} {amount}".Trim();
        }
        if (table.Equals("dynamic_unit_modifiers", StringComparison.OrdinalIgnoreCase) && int.TryParse(Value("unit_attribute_id"), out attribute))
        {
            return $"Changing {FriendlyAttribute(attribute).ToLowerInvariant()}";
        }
        var useful = fields.FirstOrDefault(field => field.Name is "effect_id" or "item_id" or "tag_id" or "kind_id" or "buff_id")?.Value;
        return string.IsNullOrWhiteSpace(useful) ? $"Row {rowId}" : $"Reference {useful}";
    }

    private static List<CatalogGameplayLink> ReadSkillGameplayLinks(SqliteConnection connection, uint skillId, string language)
    {
        if (ReadColumns(connection, "skill_effects").Count == 0 || ReadColumns(connection, "effects").Count == 0) return [];
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT se.effect_id, se.start_level, se.end_level, se.chance, e.actual_type, e.actual_id
              FROM skill_effects se
              JOIN effects e ON e.id = se.effect_id
             WHERE se.skill_id = @id
             ORDER BY COALESCE(se.start_level, 0), se.id;
            """;
        command.Parameters.AddWithValue("@id", skillId);
        using var reader = command.ExecuteReader();
        var rawLinks = new List<(uint EffectId, int? Start, int? End, int? Chance, string Type, uint ActualId)>();
        while (reader.Read())
        {
            rawLinks.Add((Convert.ToUInt32(reader.GetValue(0), CultureInfo.InvariantCulture), ReadNullableInteger(reader.GetValue(1)), ReadNullableInteger(reader.GetValue(2)), ReadNullableInteger(reader.GetValue(3)), reader.GetString(4), Convert.ToUInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }
        reader.Close();

        var result = new List<CatalogGameplayLink>();
        foreach (var raw in rawLinks)
        {
            result.Add(raw.Type.Equals("BuffEffect", StringComparison.OrdinalIgnoreCase)
                ? ReadBuffGameplayLink(connection, raw.EffectId, raw.ActualId, raw.Start, raw.End, raw.Chance, language)
                : ReadEffectGameplayLink(raw.EffectId, raw.ActualId, raw.Type, raw.Start, raw.End, raw.Chance));
        }
        return result;
    }

    private static CatalogGameplayLink ReadBuffGameplayLink(SqliteConnection connection, uint effectId, uint buffEffectId, int? start, int? end, int? skillChance, string language)
    {
        if (ReadColumns(connection, "buff_effects").Count == 0) return ReadEffectGameplayLink(effectId, buffEffectId, "BuffEffect", start, end, skillChance);
        using var effectCommand = connection.CreateCommand();
        effectCommand.CommandText = "SELECT buff_id, chance, stack FROM buff_effects WHERE id = @id LIMIT 1;";
        effectCommand.Parameters.AddWithValue("@id", buffEffectId);
        using var effectReader = effectCommand.ExecuteReader();
        if (!effectReader.Read()) return ReadEffectGameplayLink(effectId, buffEffectId, "BuffEffect", start, end, skillChance);
        var buffId = Convert.ToUInt32(effectReader.GetValue(0), CultureInfo.InvariantCulture);
        var applicationChance = ReadNullableInteger(effectReader.GetValue(1));
        var stack = ReadNullableInteger(effectReader.GetValue(2));
        effectReader.Close();

        var name = ReadLocalizedName(connection, "buffs", buffId, language) ?? $"Buff {buffId}";
        var link = new CatalogGameplayLink
        {
            Title = $"Applies {name}",
            Summary = $"{DescribeLevelRange(start, end)} The skill link and buff effect both participate in deciding when this happens.",
            TargetTable = "buffs",
            TargetId = buffId,
            ActionLabel = $"Open and change {name}"
        };
        link.Facts.Add(new CatalogGameplayFact { Label = "Application chance", Value = $"{applicationChance ?? skillChance ?? 100}%", Help = "The chance that this buff is applied after the skill effect is selected." });
        if (stack is > 0) link.Facts.Add(new CatalogGameplayFact { Label = "Stacks applied", Value = stack.Value.ToString(CultureInfo.InvariantCulture) });

        if (ReadColumns(connection, "buffs").Count > 0)
        {
            using var buffCommand = connection.CreateCommand();
            buffCommand.CommandText = "SELECT duration, level_duration, init_min_charge, init_max_charge, max_stack FROM buffs WHERE id = @id LIMIT 1;";
            buffCommand.Parameters.AddWithValue("@id", buffId);
            using var buffReader = buffCommand.ExecuteReader();
            if (buffReader.Read())
            {
                var duration = ReadNullableInteger(buffReader.GetValue(0)) ?? 0;
                var levelDuration = ReadNullableInteger(buffReader.GetValue(1)) ?? 0;
                var minCharge = ReadNullableInteger(buffReader.GetValue(2)) ?? 0;
                var maxCharge = ReadNullableInteger(buffReader.GetValue(3)) ?? 0;
                var maxStack = ReadNullableInteger(buffReader.GetValue(4)) ?? 0;
                if (minCharge > 0 || maxCharge > 0) link.Facts.Add(new CatalogGameplayFact { Label = "Protection / charge", Value = minCharge == maxCharge ? minCharge.ToString("N0", CultureInfo.InvariantCulture) : $"{minCharge:N0}–{maxCharge:N0}", Help = "For shield-style buffs, this is the damage capacity before the buff ends." });
                link.Facts.Add(new CatalogGameplayFact { Label = "Duration", Value = duration == 0 && levelDuration == 0 ? (maxCharge > 0 ? "Until protection is used" : "No fixed timeout") : $"{duration:N0} ms + {levelDuration:N0} ms per ability level", Help = "Base duration plus the per-level duration configured on the buff." });
                if (maxStack > 1) link.Facts.Add(new CatalogGameplayFact { Label = "Maximum stacks", Value = maxStack.ToString(CultureInfo.InvariantCulture) });
            }
        }

        if (ReadColumns(connection, "unit_modifiers").Count > 0)
        {
            using var modifierCommand = connection.CreateCommand();
            modifierCommand.CommandText = "SELECT unit_attribute_id, unit_modifier_type_id, value, linear_level_bonus FROM unit_modifiers WHERE owner_type = 'Buff' AND owner_id = @id ORDER BY id;";
            modifierCommand.Parameters.AddWithValue("@id", buffId);
            using var modifierReader = modifierCommand.ExecuteReader();
            while (modifierReader.Read())
            {
                var attribute = Convert.ToInt32(modifierReader.GetValue(0), CultureInfo.InvariantCulture);
                var type = Convert.ToInt32(modifierReader.GetValue(1), CultureInfo.InvariantCulture);
                var value = Convert.ToInt32(modifierReader.GetValue(2), CultureInfo.InvariantCulture);
                var perLevel = Convert.ToInt32(modifierReader.GetValue(3), CultureInfo.InvariantCulture);
                var suffix = type == 1 ? "%" : string.Empty;
                var levelText = perLevel == 0 ? string.Empty : $" ({perLevel:+#;-#;0}{suffix} per level)";
                link.Facts.Add(new CatalogGameplayFact { Label = FriendlyAttribute(attribute), Value = $"{value:+#;-#;0}{suffix}{levelText}", Help = "A character attribute changed while this buff is active." });
            }
        }
        return link;
    }

    private static CatalogGameplayLink ReadEffectGameplayLink(uint effectId, uint actualId, string actualType, int? start, int? end, int? chance)
    {
        var effectName = actualType.EndsWith("Effect", StringComparison.OrdinalIgnoreCase) ? actualType[..^6] : actualType;
        return new CatalogGameplayLink
        {
            Title = $"Runs {FriendlyName(effectName)} behavior",
            Summary = DescribeLevelRange(start, end),
            TargetTable = PascalTypeToTable(actualType),
            TargetId = actualId,
            ActionLabel = $"Open this {FriendlyName(effectName).ToLowerInvariant()} effect",
            Facts =
            [
                new CatalogGameplayFact { Label = "Application chance", Value = $"{chance ?? 100}%" },
                new CatalogGameplayFact { Label = "Effect definition", Value = $"{effectId} → {actualType} {actualId}", Help = "The generic effect ID points to this specific type of gameplay effect." }
            ]
        };
    }

    private static string? ReadLocalizedName(SqliteConnection connection, string table, uint id, string language)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaselineVerifier.QuoteIdentifier(language)} FROM localized_texts WHERE tbl_name = @table AND tbl_column_name IN ('name', 'title') AND idx = @id ORDER BY CASE tbl_column_name WHEN 'name' THEN 0 ELSE 1 END LIMIT 1;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@id", id);
        var value = command.ExecuteScalar();
        return IsCompactNull(value) ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static int? ReadNullableInteger(object? value)
    {
        if (IsCompactNull(value)) return null;
        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : null;
    }

    private static string DescribeLevelRange(int? start, int? end) => (start, end) switch
    {
        (null or 0, null or 0) => "Used at every skillset level.",
        (_, null or 0) => $"Used from skillset level {start} onward.",
        (null or 0, _) => $"Used through skillset level {end}.",
        _ => $"Used at skillset levels {start}–{end}."
    };

    private static string PascalTypeToTable(string type)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < type.Length; index++)
        {
            if (index > 0 && char.IsUpper(type[index])) builder.Append('_');
            builder.Append(char.ToLowerInvariant(type[index]));
        }
        return builder.Append('s').ToString();
    }

    private static string FriendlyAttribute(int id) => id switch
    {
        6 => "Maximum health",
        7 => "Maximum mana",
        8 => "Physical defense",
        10 => "Movement speed",
        56 => "Healing received",
        58 => "Incoming damage",
        64 => "Magic defense",
        71 => "Casting time",
        74 => "Global cooldown",
        94 => "Stealth detection range",
        120 => "Healing power",
        _ => $"Character attribute {id}"
    };

    private static string FormatValue(object value) => value switch
    {
        byte[] bytes => $"{bytes.Length} bytes of binary data",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    public static bool IsCompactNull(object? value) => value is null or DBNull ||
        value is string text && text.Trim().Equals("--- :null", StringComparison.OrdinalIgnoreCase);

    private static bool ReadBoolean(object value) => value switch
    {
        bool boolean => boolean,
        string text => text.Equals("t", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1",
        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0
    };

    public static string FriendlyName(string value)
    {
        var builder = new StringBuilder();
        foreach (var part in value.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(part.Equals("id", StringComparison.OrdinalIgnoreCase) ? "ID" : char.ToUpperInvariant(part[0]) + part[1..]);
        }
        return builder.ToString();
    }

    public static string? Describe(string name)
    {
        if (s_help.TryGetValue(name, out var help)) return help;
        if (name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)) return "Links to another game-data entry. Use Search to inspect that ID before changing it.";
        if (name.EndsWith("_time", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_delay", StringComparison.OrdinalIgnoreCase)) return "A timing value. This table commonly stores timing in milliseconds.";
        return null;
    }

    public static string? ReferenceTableFor(string name)
    {
        var normalized = name.ToLowerInvariant();
        if (normalized is "item_grade_id" or "max_item_grade_id") return "item_grades";
        if (IsItemReferenceName(normalized)) return "items";
        return normalized switch
        {
            "skill_id" or "end_skill_id" or "cooldown_skill_id" => "skills",
            "buff_id" or "toggle_buff_id" or "channeling_buff_id" or "require_buff_id" or "link_buff_id" or "transform_buff_id" or "crowd_buff_id" => "buffs",
            "effect_id" => "effects",
            "craft_id" => "crafts",
            "craft_pack_id" => "craft_packs",
            "npc_id" => "npcs",
            "doodad_id" or "req_doodad_id" or "required_doodad_id" or "channeling_doodad_id" => "doodad_almighties",
            "zone_id" => "zones",
            "quest_id" => "quest_contexts",
            _ => null
        };
    }

    private static bool IsItemReferenceName(string name)
    {
        if (name is "item_id" or "consume_item_id" or "item_template_id")
        {
            return true;
        }
        if (!name.EndsWith("_item_id", StringComparison.Ordinal))
        {
            return false;
        }
        return !new[] { "grade", "pack", "set", "category", "impl", "container", "conversion", "conv" }
            .Any(part => name.Contains(part, StringComparison.Ordinal));
    }

    private static bool IsBoolean(string name, string type, string? value) =>
        type.Equals("NUM", StringComparison.OrdinalIgnoreCase) &&
        (value is "t" or "f" or "T" or "F" or "true" or "false" or "True" or "False" || s_booleanNames.Contains(name) ||
         value is "0" or "1" && (name.StartsWith("is_") || name.StartsWith("use_") || name.StartsWith("can_") || name.StartsWith("need_") || name.StartsWith("target_") || name.StartsWith("source_")));

    private static bool IsEssential(string table, string name) =>
        name is "id" or "name" or "title" or "desc" or "web_desc" or "icon_id" or "model" ||
        table.Equals("skills", StringComparison.OrdinalIgnoreCase) && name is "show" or "ability_id" or "ability_level" or "mana_cost" or "cooldown_time" or "casting_time" or "min_range" or "max_range" or "target_type_id" or "target_relation_id" or "target_area_count" or "target_area_radius" or "consume_lp" ||
        table.Equals("buffs", StringComparison.OrdinalIgnoreCase) && name is "duration" or "level_duration" or "init_min_charge" or "init_max_charge" or "max_stack" or "damage_absorption_per_hit" or "damage_absorption_type_id" ||
        table.Equals("items", StringComparison.OrdinalIgnoreCase) && name is "price" or "refund" or "max_stack_size" or "level" or "item_grade_id";

    private static string ClassifyGroup(string table, string name)
    {
        if (table.Equals("skills", StringComparison.OrdinalIgnoreCase))
        {
            if (name is "mana_cost" or "consume_lp" or "cost") return "Resource costs";
            if (name.Contains("time", StringComparison.OrdinalIgnoreCase) || name.Contains("delay", StringComparison.OrdinalIgnoreCase) || name.Contains("cooldown", StringComparison.OrdinalIgnoreCase) || name.Contains("tick", StringComparison.OrdinalIgnoreCase)) return "Casting & timing";
            if (name.StartsWith("target_", StringComparison.OrdinalIgnoreCase) || name.Contains("range", StringComparison.OrdinalIgnoreCase) || name.Contains("angle", StringComparison.OrdinalIgnoreCase)) return "Targets & area";
        }
        if (table.Equals("buffs", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("duration", StringComparison.OrdinalIgnoreCase) || name.Contains("charge", StringComparison.OrdinalIgnoreCase) || name.Contains("absorption", StringComparison.OrdinalIgnoreCase) || name.Contains("stack", StringComparison.OrdinalIgnoreCase)) return "Strength & duration";
            if (name.StartsWith("remove_", StringComparison.OrdinalIgnoreCase)) return "When the buff ends";
            if (name.Contains("immune", StringComparison.OrdinalIgnoreCase) || name is "silence" or "root" or "sleep" or "stun" or "stealth") return "Status & immunity";
        }
        return name switch
    {
        "id" or "name" or "title" or "desc" or "web_desc" or "show" or "icon_id" or "model" => "Identity & presentation",
        "mana_cost" or "cost" or "price" or "consume_lp" => "Costs",
        _ when name.Contains("time", StringComparison.OrdinalIgnoreCase) || name.Contains("delay", StringComparison.OrdinalIgnoreCase) || name.Contains("cooldown", StringComparison.OrdinalIgnoreCase) || name.Contains("tick", StringComparison.OrdinalIgnoreCase) => "Timing",
        _ when name.StartsWith("target_", StringComparison.OrdinalIgnoreCase) || name.Contains("range", StringComparison.OrdinalIgnoreCase) || name.Contains("angle", StringComparison.OrdinalIgnoreCase) => "Targeting & range",
        _ when name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) => "Linked data",
        _ when name.StartsWith("source_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("need_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("can_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("allow_", StringComparison.OrdinalIgnoreCase) => "Rules & requirements",
        _ => "Other settings"
    };
    }

    private static (string Kind, string Label) ClassifyTable(string table) => table switch
    {
        "items" => ("item", "Item"),
        "crafts" => ("recipe", "Recipe"),
        "doodad_almighties" => ("workbench", "World object / workbench"),
        "npcs" => ("npc", "NPC"),
        "skills" => ("skill", "Skill"),
        "buffs" => ("buff", "Buff"),
        "quest_contexts" or "quest_names" => ("quest", "Quest"),
        "achievements" => ("achievement", "Achievement"),
        "appellations" => ("achievement", "Title"),
        _ => ("other", FriendlyName(table.TrimEnd('s')))
    };

    private static string DuplicateNote(string table) => table switch
    {
        "skills" => "The skill and its effects, reagents, products, and direct use requirements will be copied. Existing icons, animations, buffs, and effect definitions remain linked.",
        "crafts" => "Use Recipe maker so ingredients, products, skill rows, and workbench relationships are copied safely.",
        "doodad_almighties" => "Use Workbench maker so function groups and crafting relationships are copied safely.",
        _ => "The main record and all translated text will be copied. Referenced models, icons, effects, and other assets remain linked to their existing definitions."
    };
}
