using System.Globalization;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class ItemGameplayService
{
    public ItemGameplayProfile? GetProfile(string compactPath, uint itemId, string language = "en_us")
    {
        CompactCatalogService.ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        var profile = ReadItem(connection, itemId, language);
        if (profile is null)
        {
            return null;
        }

        var equipment = ReadEquipment(connection, itemId);
        if (equipment is null)
        {
            profile.Effects = ReadEffects(compactPath, language, profile, null);
            return profile;
        }

        profile.GearKind = equipment.Kind;
        profile.EquipmentType = equipment.Type;
        profile.SlotTypeId = equipment.SlotTypeId;
        profile.Enchantable = equipment.Enchantable;
        profile.Repairable = equipment.Repairable;
        profile.DurabilityMultiplier = equipment.DurabilityMultiplier;
        profile.AttackSpeed = equipment.AttackSpeed;
        profile.DamageScale = equipment.DamageScale;
        profile.MaximumRange = equipment.MaximumRange;
        profile.ArmorBasisPoints = equipment.ArmorBasisPoints;
        profile.MagicResistanceBasisPoints = equipment.MagicResistanceBasisPoints;
        profile.AttributeModifierSetId = equipment.ModifierSetId;
        profile.StatWeights = ReadStatWeights(connection, equipment.ModifierSetId);
        profile.Effects = ReadEffects(compactPath, language, profile, equipment);
        if (equipment.EquipmentSetId > 0)
        {
            profile.EquipmentSet = ReadEquipmentSet(connection, compactPath, equipment.EquipmentSetId, language);
        }
        return profile;
    }

    private static ItemGameplayProfile? ReadItem(SqliteConnection connection, uint itemId, string language)
    {
        using var command = connection.CreateCommand();
        var languageColumn = BaselineVerifier.QuoteIdentifier(language);
        command.CommandText = $"""
            SELECT i.id,
                   COALESCE(NULLIF(name_text.{languageColumn}, ''), i.name, ''),
                   COALESCE(NULLIF(description_text.{languageColumn}, ''), i.description, ''),
                   COALESCE(i.level, 0),
                   COALESCE(i.level_requirement, 0),
                   COALESCE(i.gradable, 0),
                   COALESCE(i.fixed_grade, 0),
                   COALESCE(i.buff_id, 0),
                   COALESCE(i.use_skill_id, 0)
              FROM items i
              LEFT JOIN localized_texts name_text
                ON name_text.tbl_name = 'items' AND name_text.tbl_column_name = 'name' AND name_text.idx = i.id
              LEFT JOIN localized_texts description_text
                ON description_text.tbl_name = 'items' AND description_text.tbl_column_name = 'description' AND description_text.idx = i.id
             WHERE i.id = @id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", itemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ItemGameplayProfile
        {
            Id = ReadUInt(reader, 0),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            ItemLevel = ReadInt(reader, 3),
            RequiredLevel = ReadInt(reader, 4),
            Gradable = ReadBool(reader, 5),
            FixedGrade = ReadInt(reader, 6),
            Effects =
            [
                new ItemLinkedEffect { Source = "Item buff", TargetTable = "buffs", Id = ReadUInt(reader, 7) },
                new ItemLinkedEffect { Source = "Use skill", TargetTable = "skills", Id = ReadUInt(reader, 8) }
            ]
        };
    }

    private static EquipmentData? ReadEquipment(SqliteConnection connection, uint itemId)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(iw.mod_set_id, 0), COALESCE(iw.eiset_id, 0), COALESCE(iw.base_enchantable, 0),
                       COALESCE(iw.repairable, 0), COALESCE(iw.durability_multiplier, 0),
                       COALESCE(h.name, ''), COALESCE(h.code, ''), COALESCE(h.slot_type_id, 0), COALESCE(h.speed, 0),
                       COALESCE(h.damage_scale, 0), COALESCE(h.max_range, 0), COALESCE(h.item_proc_id, 0),
                       COALESCE(iw.recharge_buff_id, 0)
                  FROM item_weapons iw
                  LEFT JOIN holdables h ON h.id = iw.holdable_id
                 WHERE iw.item_id = @id
                 LIMIT 1;
                """;
            command.Parameters.AddWithValue("@id", itemId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new EquipmentData
                {
                    Kind = "Weapon",
                    Type = FriendlyWeaponType(reader.GetString(5), reader.GetString(6)),
                    ModifierSetId = ReadUInt(reader, 0), EquipmentSetId = ReadUInt(reader, 1),
                    Enchantable = ReadBool(reader, 2), Repairable = ReadBool(reader, 3), DurabilityMultiplier = ReadInt(reader, 4),
                    SlotTypeId = ReadUInt(reader, 7), AttackSpeed = ReadInt(reader, 8), DamageScale = ReadInt(reader, 9),
                    MaximumRange = ReadInt(reader, 10), ProcId = ReadUInt(reader, 11), RechargeBuffId = ReadUInt(reader, 12)
                };
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(ia.mod_set_id, 0), COALESCE(ia.eiset_id, 0), COALESCE(ia.base_enchantable, 0),
                       COALESCE(ia.repairable, 0), COALESCE(ia.durability_multiplier, 0),
                       COALESCE(ia.type_id, 0), COALESCE(ia.slot_type_id, 0), COALESCE(w.armor_bp, 0),
                       COALESCE(w.magic_resistance_bp, 0), COALESCE(ia.recharge_buff_id, 0)
                  FROM item_armors ia
                  LEFT JOIN wearables w ON w.armor_type_id = ia.type_id AND w.slot_type_id = ia.slot_type_id
                 WHERE ia.item_id = @id
                 LIMIT 1;
                """;
            command.Parameters.AddWithValue("@id", itemId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new EquipmentData
                {
                    Kind = "Armor", Type = ArmorTypeName(ReadUInt(reader, 5)), ModifierSetId = ReadUInt(reader, 0),
                    EquipmentSetId = ReadUInt(reader, 1), Enchantable = ReadBool(reader, 2), Repairable = ReadBool(reader, 3),
                    DurabilityMultiplier = ReadInt(reader, 4), SlotTypeId = ReadUInt(reader, 6), ArmorBasisPoints = ReadInt(reader, 7),
                    MagicResistanceBasisPoints = ReadInt(reader, 8), RechargeBuffId = ReadUInt(reader, 9)
                };
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(ia.mod_set_id, 0), COALESCE(ia.eiset_id, 0), COALESCE(ia.repairable, 0),
                       COALESCE(ia.durability_multiplier, 0), COALESCE(ia.type_id, 0), COALESCE(ia.slot_type_id, 0),
                       COALESCE(ia.recharge_buff_id, 0)
                  FROM item_accessories ia
                 WHERE ia.item_id = @id
                 LIMIT 1;
                """;
            command.Parameters.AddWithValue("@id", itemId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new EquipmentData
                {
                    Kind = "Accessory", Type = $"Accessory type {ReadUInt(reader, 4)}", ModifierSetId = ReadUInt(reader, 0),
                    EquipmentSetId = ReadUInt(reader, 1), Enchantable = false, Repairable = ReadBool(reader, 2),
                    DurabilityMultiplier = ReadInt(reader, 3), SlotTypeId = ReadUInt(reader, 5), RechargeBuffId = ReadUInt(reader, 6)
                };
            }
        }
        return null;
    }

    private static List<ItemStatWeight> ReadStatWeights(SqliteConnection connection, uint modifierSetId)
    {
        if (modifierSetId == 0) return [];
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT str_weight, dex_weight, sta_weight, int_weight, spi_weight FROM equip_item_attr_modifiers WHERE id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", modifierSetId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return [];
        var weights = new[]
        {
            ("Strength", ReadInt(reader, 0)), ("Agility", ReadInt(reader, 1)), ("Stamina", ReadInt(reader, 2)),
            ("Intelligence", ReadInt(reader, 3)), ("Spirit", ReadInt(reader, 4))
        };
        var total = weights.Sum(weight => Math.Max(0, weight.Item2));
        if (total == 0) return [];
        return weights.Where(weight => weight.Item2 > 0)
            .Select(weight => new ItemStatWeight(weight.Item1, weight.Item2, (int)Math.Round(weight.Item2 * 100d / total)))
            .ToList();
    }

    private static List<ItemLinkedEffect> ReadEffects(string compactPath, string language, ItemGameplayProfile profile, EquipmentData? equipment)
    {
        var requested = profile.Effects.Where(effect => effect.Id > 0).ToList();
        if (equipment?.RechargeBuffId > 0) requested.Add(new ItemLinkedEffect { Source = "Recharge buff", TargetTable = "buffs", Id = equipment.RechargeBuffId });
        if (equipment?.ProcId > 0) requested.Add(new ItemLinkedEffect { Source = "Weapon proc", TargetTable = "item_procs", Id = equipment.ProcId });
        return requested
            .GroupBy(effect => (effect.TargetTable, effect.Id))
            .Select(group => ReadEffect(compactPath, group.First().Source, group.Key.TargetTable, group.Key.Id, language))
            .ToList();
    }

    private static ItemEquipmentSet ReadEquipmentSet(SqliteConnection connection, string compactPath, uint setId, string language)
    {
        var set = new ItemEquipmentSet { Id = setId, Name = $"Equipment set {setId}" };
        using (var command = connection.CreateCommand())
        {
            var languageColumn = BaselineVerifier.QuoteIdentifier(language);
            command.CommandText = $"""
                SELECT COALESCE(NULLIF(name_text.{languageColumn}, ''), s.name, ''),
                       COALESCE(NULLIF(description_text.{languageColumn}, ''), s.description, '')
                  FROM equip_item_sets s
                  LEFT JOIN localized_texts name_text ON name_text.tbl_name = 'equip_item_sets' AND name_text.tbl_column_name = 'name' AND name_text.idx = s.id
                  LEFT JOIN localized_texts description_text ON description_text.tbl_name = 'equip_item_sets' AND description_text.tbl_column_name = 'description' AND description_text.idx = s.id
                 WHERE s.id = @id LIMIT 1;
                """;
            command.Parameters.AddWithValue("@id", setId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                set.Name = string.IsNullOrWhiteSpace(reader.GetString(0)) ? set.Name : reader.GetString(0);
                set.Description = reader.GetString(1);
            }
        }

        using (var command = connection.CreateCommand())
        {
            var languageColumn = BaselineVerifier.QuoteIdentifier(language);
            command.CommandText = $"""
                WITH set_items AS (
                    SELECT item_id, 'Weapon' AS gear_kind FROM item_weapons WHERE eiset_id = @id
                    UNION SELECT item_id, 'Armor' FROM item_armors WHERE eiset_id = @id
                    UNION SELECT item_id, 'Accessory' FROM item_accessories WHERE eiset_id = @id
                )
                SELECT i.id, COALESCE(NULLIF(lt.{languageColumn}, ''), i.name, ''), si.gear_kind
                  FROM set_items si
                  JOIN items i ON i.id = si.item_id
                  LEFT JOIN localized_texts lt ON lt.tbl_name = 'items' AND lt.tbl_column_name = 'name' AND lt.idx = i.id
                 ORDER BY LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), i.name, '')), i.id;
                """;
            command.Parameters.AddWithValue("@id", setId);
            using var reader = command.ExecuteReader();
            var pieces = new List<EquipmentSetPiece>();
            while (reader.Read()) pieces.Add(new EquipmentSetPiece(ReadUInt(reader, 0), reader.GetString(1), reader.GetString(2)));
            set.Pieces = pieces;
        }

        var bonusRows = new List<(int Pieces, uint BuffId, uint ProcId)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT num_pieces, COALESCE(buff_id, 0), COALESCE(proc_id, 0) FROM equip_item_set_bonuses WHERE equip_item_set_id = @id ORDER BY num_pieces, id;";
            command.Parameters.AddWithValue("@id", setId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) bonusRows.Add((ReadInt(reader, 0), ReadUInt(reader, 1), ReadUInt(reader, 2)));
        }
        set.Bonuses = bonusRows.Select(row => new ItemEquipmentSetBonus
        {
            RequiredPieces = row.Pieces,
            Buff = row.BuffId == 0 ? null : ReadEffect(compactPath, "Set bonus", "buffs", row.BuffId, language),
            Proc = row.ProcId == 0 ? null : ReadEffect(compactPath, "Set proc", "item_procs", row.ProcId, language)
        }).ToList();
        return set;
    }

    private static ItemLinkedEffect ReadEffect(string compactPath, string source, string table, uint id, string language)
    {
        var record = new CatalogRecordService().GetRecord(compactPath, table, id, language);
        if (record is null)
        {
            return new ItemLinkedEffect { Source = source, TargetTable = table, Id = id, Name = $"{CatalogRecordService.FriendlyName(table.TrimEnd('s'))} {id}" };
        }
        var description = record.Localizations
            .Where(field => field.Field is "description" or "desc" or "web_desc" or "tooltip")
            .Select(field => field.Values.GetValueOrDefault(language))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var facts = record.RelatedSections.SelectMany(section => section.Rows)
            .Select(row => row.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        return new ItemLinkedEffect { Source = source, TargetTable = table, Id = id, Name = record.Name, Description = description, Facts = facts };
    }

    private static uint ReadUInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : Convert.ToUInt32(reader.GetValue(index), CultureInfo.InvariantCulture);
    private static int ReadInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : Convert.ToInt32(reader.GetValue(index), CultureInfo.InvariantCulture);
    private static bool ReadBool(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return false;
        var value = Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
        return value is "1" or "t" or "T" or "true" or "True";
    }

    private static string FriendlyWeaponType(string name, string code)
    {
        var translated = name switch
        {
            "한손지팡이" => "Scepter (one-handed staff)",
            "양손지팡이" => "Staff (two-handed)",
            "단검" => "Dagger",
            "한손검" => "Longsword",
            "양손검" => "Greatsword",
            "활" => "Bow",
            "방패" => "Shield",
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(translated)) return translated;
        if (string.IsNullOrWhiteSpace(code)) return string.IsNullOrWhiteSpace(name) ? "Weapon" : name;
        var words = code.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Equals("1h", StringComparison.OrdinalIgnoreCase) ? "One-handed" : word.Equals("2h", StringComparison.OrdinalIgnoreCase) ? "Two-handed" : char.ToUpperInvariant(word[0]) + word[1..]);
        return string.Join(' ', words);
    }

    private static string ArmorTypeName(uint typeId) => typeId switch
    {
        1 => "Cloth armor",
        2 => "Leather armor",
        3 => "Plate armor",
        _ => $"Armor type {typeId}"
    };

    private sealed class EquipmentData
    {
        public string Kind { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public uint ModifierSetId { get; set; }
        public uint EquipmentSetId { get; set; }
        public uint SlotTypeId { get; set; }
        public bool Enchantable { get; set; }
        public bool Repairable { get; set; }
        public int DurabilityMultiplier { get; set; }
        public int AttackSpeed { get; set; }
        public int DamageScale { get; set; }
        public int MaximumRange { get; set; }
        public int ArmorBasisPoints { get; set; }
        public int MagicResistanceBasisPoints { get; set; }
        public uint RechargeBuffId { get; set; }
        public uint ProcId { get; set; }
    }
}
