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
        "target_siege", "target_water", "target_only_water", "source_mount", "source_mount_mate", "unmount",
        "gradable", "grade_enchantable", "base_enchantable", "repairable", "base_equipment", "or_unit_reqs"
    };

    private static readonly Dictionary<string, string> s_help = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "Managed automatically by Content Studio.",
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
        ["unit_attribute_id"] = "The character statistic changed by this modifier. The gameplay summary translates known values into names.",
        ["unit_modifier_type_id"] = "Zero adds a flat amount; one applies a percentage.",
        ["value"] = "Base amount added to the selected character statistic.",
        ["linear_level_bonus"] = "Extra amount added for each applicable level.",
        ["req_doodad_id"] = "The workbench or world object required to perform this action. Zero means none.",
        ["level"] = "The item's internal power level. Gear damage, defense, primary attributes, and several other values scale from this number.",
        ["level_requirement"] = "The character level required to equip or use this item. This does not set the item's combat power.",
        ["gradable"] = "Whether the item can use quality grades such as Arcane, Heroic, or Celestial. Grades multiply several gear stats.",
        ["fixed_grade"] = "For items that always use one quality, this chooses that grade. Minus one means the grade is not forced.",
        ["grade_enchantable"] = "Whether players can improve this item's quality grade through grade enchanting.",
        ["base_enchantable"] = "Whether this equipment template supports its normal enchanting or tempering behavior.",
        ["repairable"] = "Whether lost durability can be repaired.",
        ["durability_multiplier"] = "Item-specific durability scaling. 100 means the normal durability for this equipment type.",
        ["mod_set_id"] = "The primary-stat allocation profile. Content Studio manages this link when you give one item its own stat mix.",
        ["str_weight"] = "Strength's share of the primary-stat budget. Only its proportion relative to the other positive weights matters.",
        ["dex_weight"] = "Agility's share of the primary-stat budget. Only its proportion relative to the other positive weights matters.",
        ["sta_weight"] = "Stamina's share of the primary-stat budget. Only its proportion relative to the other positive weights matters.",
        ["int_weight"] = "Intelligence's share of the primary-stat budget. Only its proportion relative to the other positive weights matters.",
        ["spi_weight"] = "Spirit's share of the primary-stat budget. Only its proportion relative to the other positive weights matters.",
        ["stat_multiplier"] = "Percentage scaling applied to primary attributes for this grade or weapon type. 100 means normal strength.",
        ["item_stat_const"] = "Global primary-stat scaling used by every equipment item. Changing this rebalances the entire gear system.",
        ["holdable_stat_const"] = "Global primary-stat scaling for all weapons and shields.",
        ["wearable_stat_const"] = "Global primary-stat scaling for all armor and accessories.",
        ["stat_value_const"] = "Controls the bonus awarded when a gear profile concentrates its budget into fewer primary attributes.",
        ["speed"] = "Auto-attack delay in milliseconds for this weapon type. A lower number attacks faster.",
        ["damage_scale"] = "Random damage spread around the variable part of a hit. A value of 5 allows roughly 5% below or above that portion; it is not a flat DPS multiplier.",
        ["formula_dps"] = "Formula used to calculate physical weapon DPS from item level and grade. This affects every weapon of this type.",
        ["formula_mdps"] = "Formula used to calculate magic weapon DPS from item level and grade. This affects every weapon of this type.",
        ["formula_hdps"] = "Formula used to calculate healing power from item level and grade. This affects every weapon of this type.",
        ["formula_armor"] = "Formula used to calculate a shield or holdable's armor from item level and grade.",
        ["armor_ratio"] = "Physical-defense percentage for this armor class. 100 means the formula's normal result.",
        ["magic_resistance_ratio"] = "Magic-defense percentage for this armor class. 100 means the formula's normal result.",
        ["coverage"] = "The percentage of a full equipment budget assigned to this body slot. Chest pieces are normally higher than small slots.",
        ["armor_bp"] = "Base physical-defense weighting for this armor class and slot combination.",
        ["magic_resistance_bp"] = "Base magic-defense weighting for this armor class and slot combination.",
        ["var_holdable_dps"] = "This grade's input to physical weapon DPS formulas.",
        ["var_holdable_magic_dps"] = "This grade's input to magic weapon DPS formulas.",
        ["var_holdable_heal_dps"] = "This grade's input to healing-power formulas.",
        ["var_wearable_armor"] = "This grade's input to armor-defense formulas.",
        ["var_wearable_magic_resistance"] = "This grade's input to magic-defense formulas.",
        ["durability_value"] = "This grade's durability multiplier.",
        ["durability_ratio"] = "Shared durability multiplier for this weapon type or armor class. A value of 1 means its normal durability contribution.",
        ["formula"] = "The defense calculation using item_level and item_grade. Keep those variable names and the existing math structure unless you intentionally want to redesign the full curve."
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
                Label = FriendlyName(table, name),
                Type = type,
                Group = ClassifyGroup(table, name),
                Help = Describe(name),
                Value = value,
                IsNull = isNull,
                IsBoolean = IsBoolean(name, type, value),
                IsEssential = IsEssential(table, name),
                IsIdentity = name.Equals("id", StringComparison.OrdinalIgnoreCase),
                IsEditable = !type.Contains("BLOB", StringComparison.OrdinalIgnoreCase) && !IsStructuralBalanceKey(table, name),
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
        nameValue = FriendlyRecordName(table, id, fields, nameValue);
        return new CatalogRecord
        {
            Table = table,
            Id = id,
            Name = string.IsNullOrWhiteSpace(nameValue) ? $"Unnamed {kindLabel.ToLowerInvariant()}" : nameValue,
            Kind = kind,
            KindLabel = kindLabel,
            CanChange = true,
            CanDuplicate = true,
            DuplicateNote = DuplicateNote(table),
            Fields = fields,
            Localizations = localizations,
            RelatedSections = ReadRelatedSections(connection, table, id),
            LinkedRecords = table.Equals("items", StringComparison.OrdinalIgnoreCase) ? ReadItemLinkedRecords(connection, id) : [],
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
            "items" =>
            [
                ("item_weapons", "item_id", "Weapon template", "Weapon type, durability, enchanting, set membership, recharge effects, and other rules owned by this item.", string.Empty),
                ("item_armors", "item_id", "Armor template", "Armor class, equipment slot, durability, enchanting, set membership, and other rules owned by this item.", string.Empty),
                ("item_accessories", "item_id", "Accessory template", "Accessory type, equipment slot, durability, set membership, and other rules owned by this item.", string.Empty)
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
                    var modifierLink = table.Equals("items", StringComparison.OrdinalIgnoreCase) && name.Equals("mod_set_id", StringComparison.OrdinalIgnoreCase);
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
                        IsEditable = !type.Contains("BLOB", StringComparison.OrdinalIgnoreCase) && !modifierLink,
                        ReferenceTable = ReferenceTableFor(name)
                    });
                }
                var rowId = Convert.ToUInt32(reader["id"], CultureInfo.InvariantCulture);
                rows.Add(new CatalogRelatedRow { Id = rowId, Label = BuildRelatedRowLabel(definition.Table, rowId, fields), Fields = fields });
            }
            if (rows.Count > 0)
                sections.Add(new CatalogRelatedSection { Table = definition.Table, OwnerColumn = definition.Owner, Title = definition.Title, Description = definition.Description, IsEquipmentTemplate = table.Equals("items", StringComparison.OrdinalIgnoreCase), Rows = rows });
        }
        return sections;
    }

    private static List<CatalogLinkedRecord> ReadItemLinkedRecords(SqliteConnection connection, uint itemId)
    {
        foreach (var gearTable in new[] { "item_weapons", "item_armors", "item_accessories" })
        {
            if (ReadColumns(connection, gearTable).Count == 0) continue;
            using var gearCommand = connection.CreateCommand();
            gearCommand.CommandText = $"SELECT id, COALESCE(mod_set_id, 0) FROM {BaselineVerifier.QuoteIdentifier(gearTable)} WHERE item_id = @id LIMIT 1;";
            gearCommand.Parameters.AddWithValue("@id", itemId);
            using var gearReader = gearCommand.ExecuteReader();
            if (!gearReader.Read()) continue;
            var linkRowId = Convert.ToUInt32(gearReader.GetValue(0), CultureInfo.InvariantCulture);
            var modifierId = Convert.ToUInt32(gearReader.GetValue(1), CultureInfo.InvariantCulture);
            gearReader.Close();

            var columns = ReadColumns(connection, "equip_item_attr_modifiers");
            if (columns.Count == 0) return [];
            var fields = new List<CatalogRecordField>();
            if (modifierId > 0)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM equip_item_attr_modifiers WHERE id = @id LIMIT 1;";
                command.Parameters.AddWithValue("@id", modifierId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        var name = reader.GetName(index);
                        var type = columns.First(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Type;
                        var rawValue = reader.IsDBNull(index) ? null : reader.GetValue(index);
                        var isNull = IsCompactNull(rawValue);
                        fields.Add(CreateLinkedField(name, type, isNull ? null : FormatValue(rawValue!), isNull));
                    }
                }
            }
            else
            {
                foreach (var column in columns)
                {
                    var value = column.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ? "Assigned when saved" : column.Name.Equals("alias", StringComparison.OrdinalIgnoreCase) ? $"custom_item_{itemId}" : "0";
                    fields.Add(CreateLinkedField(column.Name, column.Type, value, false));
                }
            }

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = """
                SELECT COUNT(*) FROM (
                    SELECT item_id FROM item_weapons WHERE mod_set_id = @id
                    UNION ALL SELECT item_id FROM item_armors WHERE mod_set_id = @id
                    UNION ALL SELECT item_id FROM item_accessories WHERE mod_set_id = @id
                );
                """;
            countCommand.Parameters.AddWithValue("@id", modifierId);
            var referenceCount = modifierId == 0 ? 0 : Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            return
            [
                new CatalogLinkedRecord
                {
                    Table = "equip_item_attr_modifiers",
                    SourceId = modifierId,
                    Title = "Primary attribute mix",
                    Description = "Strength, Agility, Stamina, Intelligence, and Spirit divide one calculated stat budget. A private copy changes this item without silently changing other gear.",
                    LinkTable = gearTable,
                    LinkSourceId = linkRowId,
                    LinkColumn = "mod_set_id",
                    ReferenceCount = referenceCount,
                    Fields = fields
                }
            ];
        }
        return [];
    }

    private static CatalogRecordField CreateLinkedField(string name, string type, string? value, bool isNull) => new()
    {
        Name = name,
        Label = FriendlyName(name),
        Type = type,
        Group = "Primary attribute mix",
        Help = Describe(name),
        Value = value,
        IsNull = isNull,
        IsBoolean = false,
        IsEssential = true,
        IsIdentity = name.Equals("id", StringComparison.OrdinalIgnoreCase),
        IsEditable = !name.Equals("id", StringComparison.OrdinalIgnoreCase)
    };

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
        return table.ToLowerInvariant() switch
        {
            "skill_effects" or "tooltip_skill_effects" or "buff_tick_effects" => "Effect behavior",
            "skill_reagents" => "Consumed item",
            "skill_products" => "Created item",
            "tagged_skills" or "tagged_buffs" => "Gameplay tag",
            "unit_reqs" => "Use requirement",
            "buff_unit_modifiers" => "Connected buff",
            _ => $"Connected {FriendlyName(table.TrimEnd('s')).ToLowerInvariant()}"
        };
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

        var name = ReadLocalizedName(connection, "buffs", buffId, language) ?? "Unnamed buff";
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
                new CatalogGameplayFact { Label = "Behavior type", Value = FriendlyName(effectName), Help = "This is the specific gameplay behavior run by the effect step." }
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
        _ => "Other character attribute"
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
        var specialized = value.ToLowerInvariant() switch
        {
            "str_weight" => "Strength share",
            "dex_weight" => "Agility share",
            "sta_weight" => "Stamina share",
            "int_weight" => "Intelligence share",
            "spi_weight" => "Spirit share",
            "eiset_id" => "Equipment set",
            "mod_set_id" => "Primary stat profile",
            "holdable_id" => "Weapon type",
            "type_id" => "Armor class",
            "slot_type_id" => "Equipment slot",
            "fixed_grade" => "Fixed quality grade",
            "grade_order" => "Quality rank order",
            "speed" => "Auto-attack delay (ms)",
            "damage_scale" => "Random damage spread (%)",
            "formula_dps" => "Physical DPS formula",
            "formula_mdps" => "Magic DPS formula",
            "formula_hdps" => "Healing power formula",
            "formula_armor" => "Shield defense formula",
            "armor_ratio" => "Physical defense (%)",
            "magic_resistance_ratio" => "Magic defense (%)",
            "coverage" => "Slot budget coverage (%)",
            "armor_bp" => "Base physical defense",
            "magic_resistance_bp" => "Base magic defense",
            "var_holdable_dps" => "Physical DPS grade factor",
            "var_holdable_magic_dps" => "Magic DPS grade factor",
            "var_holdable_heal_dps" => "Healing grade factor",
            "var_wearable_armor" => "Physical defense grade factor",
            "var_wearable_magic_resistance" => "Magic defense grade factor",
            "durability_value" => "Durability grade multiplier",
            "stat_multiplier" => "Primary-stat multiplier (%)",
            "item_stat_const" => "Global primary-stat scale (%)",
            "holdable_stat_const" => "Weapon primary-stat scale (%)",
            "wearable_stat_const" => "Armor primary-stat scale (%)",
            "stat_value_const" => "Focused-stat bonus curve (%)",
            _ => null
        };
        if (specialized is not null) return specialized;
        var builder = new StringBuilder();
        foreach (var part in value.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(char.ToUpperInvariant(part[0]) + part[1..]);
        }
        return builder.ToString();
    }

    public static string FriendlyTableName(string table) => table.ToLowerInvariant() switch
    {
        "items" => "Item",
        "item_grades" => "Item quality",
        "item_categories" => "Item category",
        "crafts" => "Recipe",
        "craft_packs" => "Recipe group",
        "doodad_almighties" => "Workbench or world object",
        "actability_categories" => "Crafting proficiency",
        "skills" => "Skill",
        "abilities" => "Skillset",
        "buffs" => "Buff",
        "effects" => "Effect",
        "npcs" => "NPC",
        "quest_contexts" or "quest_names" => "Quest",
        "zones" => "Zone",
        "equip_item_sets" => "Equipment set",
        "equip_item_attr_modifiers" => "Primary stat profile",
        "holdables" => "Weapon type",
        "wearable_kinds" => "Armor class",
        "wearable_slots" => "Equipment slot",
        _ => FriendlyName(table.EndsWith('s') ? table[..^1] : table)
    };

    private static string FriendlyName(string table, string value)
    {
        return (table.ToLowerInvariant(), value.ToLowerInvariant()) switch
        {
            ("wearable_kinds", "armor_type_id") => "Armor class",
            ("wearable_slots", "slot_type_id") => "Equipment slot",
            ("wearables", "armor_type_id") => "Armor class",
            ("wearables", "slot_type_id") => "Equipment slot",
            ("wearable_formulas", "kind_id") => "Defense type",
            ("wearable_formulas", "formula") => "Defense formula",
            _ => FriendlyName(value)
        };
    }

    private static bool IsStructuralBalanceKey(string table, string name) =>
        (table.Equals("wearable_kinds", StringComparison.OrdinalIgnoreCase) && name.Equals("armor_type_id", StringComparison.OrdinalIgnoreCase)) ||
        (table.Equals("wearable_slots", StringComparison.OrdinalIgnoreCase) && name.Equals("slot_type_id", StringComparison.OrdinalIgnoreCase)) ||
        (table.Equals("wearables", StringComparison.OrdinalIgnoreCase) && name is "armor_type_id" or "slot_type_id") ||
        (table.Equals("wearable_formulas", StringComparison.OrdinalIgnoreCase) && name.Equals("kind_id", StringComparison.OrdinalIgnoreCase));

    private static string? FriendlyRecordName(string table, uint id, IReadOnlyList<CatalogRecordField> fields, string? currentName)
    {
        if (!string.IsNullOrWhiteSpace(currentName)) return currentName;
        var armorType = FieldNumber(fields, "armor_type_id");
        var slotType = FieldNumber(fields, "slot_type_id");
        return table.ToLowerInvariant() switch
        {
            "item_configs" => "Global gear constants",
            "equip_item_attr_modifiers" => "Primary attribute profile",
            "wearable_kinds" => $"{ArmorTypeName(armorType)} balance",
            "wearable_slots" => $"{EquipmentSlotName(slotType)} slot budget",
            "wearables" => $"{ArmorTypeName(armorType)} · {EquipmentSlotName(slotType)} defense basis",
            "wearable_formulas" => FieldNumber(fields, "kind_id") switch
            {
                0 => "Physical defense formula",
                1 => "Magic defense formula",
                var formulaKind => $"Defense formula {formulaKind}"
            },
            _ => currentName
        };
    }

    private static uint FieldNumber(IEnumerable<CatalogRecordField> fields, string name) =>
        uint.TryParse(fields.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value, out var value) ? value : 0;

    private static string ArmorTypeName(uint id) => id switch
    {
        1 => "Cloth armor",
        2 => "Leather armor",
        3 => "Plate armor",
        4 => "Pet armor",
        5 => "Other armor",
        _ => "Other armor class"
    };

    private static string EquipmentSlotName(uint id) => id switch
    {
        1 => "Head", 2 => "Neck", 3 => "Chest", 4 => "Waist", 5 => "Legs", 6 => "Hands", 7 => "Feet",
        8 => "Arms", 9 => "Back", 10 => "Ear", 11 => "Finger", 12 => "Undershirt", 13 => "Underpants", 31 => "Cosplay",
        _ => "Other equipment slot"
    };

    public static string? Describe(string name)
    {
        if (s_help.TryGetValue(name, out var help)) return help;
        if (name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)) return "Links to another game-data entry. Choose it by name when Content Studio has a verified relationship for this setting.";
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
            "ability_id" => "abilities",
            "skill_id" or "end_skill_id" or "cooldown_skill_id" => "skills",
            "buff_id" or "toggle_buff_id" or "channeling_buff_id" or "require_buff_id" or "link_buff_id" or "transform_buff_id" or "crowd_buff_id" => "buffs",
            "effect_id" => "effects",
            "craft_id" => "crafts",
            "craft_pack_id" => "craft_packs",
            "npc_id" => "npcs",
            "doodad_id" or "req_doodad_id" or "required_doodad_id" or "channeling_doodad_id" => "doodad_almighties",
            "zone_id" => "zones",
            "quest_id" => "quest_contexts",
            "eiset_id" => "equip_item_sets",
            "mod_set_id" => "equip_item_attr_modifiers",
            "holdable_id" => "holdables",
            "recharge_buff_id" => "buffs",
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

    internal static bool IsBooleanField(string name, string type, string? value) => IsBoolean(name, type, value);

    private static bool IsEssential(string table, string name) =>
        name is "id" or "name" or "title" or "desc" or "web_desc" or "icon_id" or "model" ||
        table.Equals("skills", StringComparison.OrdinalIgnoreCase) && name is "show" or "ability_id" or "ability_level" or "mana_cost" or "cooldown_time" or "casting_time" or "min_range" or "max_range" or "target_type_id" or "target_relation_id" or "target_area_count" or "target_area_radius" or "consume_lp" ||
        table.Equals("buffs", StringComparison.OrdinalIgnoreCase) && name is "duration" or "level_duration" or "init_min_charge" or "init_max_charge" or "max_stack" or "damage_absorption_per_hit" or "damage_absorption_type_id" ||
        table.Equals("items", StringComparison.OrdinalIgnoreCase) && name is "price" or "refund" or "max_stack_size" or "level" or "level_requirement" or "gradable" or "fixed_grade" or "grade_enchantable" or "item_grade_id" ||
        table.Equals("equip_item_attr_modifiers", StringComparison.OrdinalIgnoreCase) && name is "alias" or "str_weight" or "dex_weight" or "sta_weight" or "int_weight" or "spi_weight" ||
        table.Equals("item_configs", StringComparison.OrdinalIgnoreCase) ||
        table.Equals("item_grades", StringComparison.OrdinalIgnoreCase) && name is "name" or "grade_order" or "var_holdable_dps" or "var_holdable_magic_dps" or "var_holdable_heal_dps" or "var_wearable_armor" or "var_wearable_magic_resistance" or "durability_value" or "stat_multiplier" ||
        table.Equals("holdables", StringComparison.OrdinalIgnoreCase) && name is "name" or "code" or "speed" or "damage_scale" or "min_range" or "max_range" or "durability_ratio" or "stat_multiplier" or "formula_dps" or "formula_mdps" or "formula_hdps" or "formula_armor" ||
        table.Equals("wearable_kinds", StringComparison.OrdinalIgnoreCase) && name is "armor_type_id" or "armor_ratio" or "magic_resistance_ratio" or "durability_ratio" ||
        table.Equals("wearable_slots", StringComparison.OrdinalIgnoreCase) && name is "slot_type_id" or "coverage" ||
        table.Equals("wearables", StringComparison.OrdinalIgnoreCase) && name is "armor_type_id" or "slot_type_id" or "armor_bp" or "magic_resistance_bp" ||
        table.Equals("wearable_formulas", StringComparison.OrdinalIgnoreCase) && name is "kind_id" or "formula";

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
        _ => ("other", FriendlyTableName(table))
    };

    private static string DuplicateNote(string table) => table switch
    {
        "skills" => "The skill and its effects, reagents, products, and direct use requirements will be copied. Existing icons, animations, buffs, and effect definitions remain linked.",
        "crafts" => "Use Recipe maker so ingredients, products, skill rows, and workbench relationships are copied safely.",
        "doodad_almighties" => "Use Workbench maker so function groups and crafting relationships are copied safely.",
        _ => "The main record and all translated text will be copied. Referenced models, icons, effects, and other assets remain linked to their existing definitions."
    };
}
