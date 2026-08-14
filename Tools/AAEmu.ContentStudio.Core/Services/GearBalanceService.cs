using System.Globalization;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class GearBalanceService
{
    public GearBalanceDashboard GetDashboard(string compactPath)
    {
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        return new GearBalanceDashboard
        {
            GlobalConstants = ReadGlobalConstants(connection),
            Grades = ReadGrades(connection),
            WeaponTypes = ReadWeaponTypes(connection),
            ArmorClasses = ReadArmorClasses(connection),
            EquipmentSlots = ReadEquipmentSlots(connection),
            ArmorFormulas = ReadArmorFormulas(connection)
        };
    }

    private static List<GearBalanceEntry> ReadGlobalConstants(SqliteConnection connection)
    {
        if (!TableExists(connection, "item_configs")) return [];
        var affectedItems = CountAllGear(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM item_configs ORDER BY id;";
        using var reader = command.ExecuteReader();
        var result = new List<GearBalanceEntry>();
        while (reader.Read())
        {
            result.Add(new GearBalanceEntry(
                "Global gear constants",
                "Base primary-stat and durability scaling used by every weapon, armor piece, and accessory.",
                "item_configs", ReadUInt(reader, 0), affectedItems));
        }
        return result;
    }

    private static List<GearBalanceEntry> ReadGrades(SqliteConnection connection)
    {
        if (!TableExists(connection, "item_grades")) return [];
        var affectedItems = CountAllGear(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, COALESCE(name, ''), COALESCE(grade_order, id) FROM item_grades ORDER BY grade_order, id;";
        using var reader = command.ExecuteReader();
        var result = new List<GearBalanceEntry>();
        while (reader.Read())
        {
            var id = ReadUInt(reader, 0);
            var name = EnglishGradeName(reader.GetString(1), id);
            result.Add(new GearBalanceEntry(
                name,
                "Damage, defense, primary-stat, durability, enchanting, and refund scaling for this quality grade.",
                "item_grades", id, affectedItems));
        }
        return result;
    }

    private static List<GearBalanceEntry> ReadWeaponTypes(SqliteConnection connection)
    {
        if (!TableExists(connection, "holdables") || !TableExists(connection, "item_weapons")) return [];
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.id, COALESCE(h.name, ''), COALESCE(h.code, ''), COUNT(iw.item_id)
              FROM holdables h
              LEFT JOIN item_weapons iw ON iw.holdable_id = h.id
             GROUP BY h.id, h.name, h.code
             ORDER BY LOWER(COALESCE(NULLIF(h.code, ''), h.name, '')), h.id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<GearBalanceEntry>();
        while (reader.Read())
        {
            var id = ReadUInt(reader, 0);
            var name = FriendlyWeaponType(reader.GetString(1), reader.GetString(2));
            result.Add(new GearBalanceEntry(
                name,
                "Shared attack delay, range, random damage spread, damage/healing formulas, durability, and primary-stat multiplier.",
                "holdables", id, ReadInt(reader, 3)));
        }
        return result;
    }

    private static List<GearBalanceEntry> ReadArmorClasses(SqliteConnection connection)
    {
        if (!TableExists(connection, "wearable_kinds")) return [];
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT wk.id, wk.armor_type_id,
                   (SELECT COUNT(*) FROM item_armors ia WHERE ia.type_id = wk.armor_type_id) +
                   (SELECT COUNT(*) FROM item_accessories ix WHERE ix.type_id = wk.armor_type_id)
              FROM wearable_kinds wk
             ORDER BY wk.armor_type_id, wk.id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<GearBalanceEntry>();
        while (reader.Read())
        {
            var id = ReadUInt(reader, 0);
            var typeId = ReadUInt(reader, 1);
            result.Add(new GearBalanceEntry(
                ArmorTypeName(typeId),
                "Shared physical-defense, magic-defense, damage-type, and durability ratios for this armor class.",
                "wearable_kinds", id, ReadInt(reader, 2)));
        }
        return result;
    }

    private static List<GearBalanceEntry> ReadEquipmentSlots(SqliteConnection connection)
    {
        if (!TableExists(connection, "wearable_slots")) return [];
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ws.id, ws.slot_type_id,
                   (SELECT COUNT(*) FROM item_armors ia WHERE ia.slot_type_id = ws.slot_type_id) +
                   (SELECT COUNT(*) FROM item_accessories ix WHERE ix.slot_type_id = ws.slot_type_id)
              FROM wearable_slots ws
             ORDER BY ws.slot_type_id, ws.id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<GearBalanceEntry>();
        while (reader.Read())
        {
            var id = ReadUInt(reader, 0);
            var slotTypeId = ReadUInt(reader, 1);
            result.Add(new GearBalanceEntry(
                SlotName(slotTypeId),
                "Shared coverage percentage that divides primary stats, defense, and durability across equipment slots.",
                "wearable_slots", id, ReadInt(reader, 2)));
        }
        return result;
    }

    private static List<GearBalanceEntry> ReadArmorFormulas(SqliteConnection connection)
    {
        if (!TableExists(connection, "wearable_formulas")) return [];
        var affectedItems = CountArmorAndAccessories(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind_id FROM wearable_formulas ORDER BY kind_id, id;";
        using var reader = command.ExecuteReader();
        var result = new List<GearBalanceEntry>();
        while (reader.Read())
        {
            var id = ReadUInt(reader, 0);
            var kind = ReadInt(reader, 1);
            var name = kind switch { 0 => "Physical defense formula", 1 => "Magic defense formula", _ => $"Wearable formula {kind}" };
            result.Add(new GearBalanceEntry(
                name,
                "Server-wide formula that converts item level and grade into wearable defense before class and slot scaling.",
                "wearable_formulas", id, affectedItems));
        }
        return result;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static int CountAllGear(SqliteConnection connection) => Count(connection,
        "SELECT (SELECT COUNT(*) FROM item_weapons) + (SELECT COUNT(*) FROM item_armors) + (SELECT COUNT(*) FROM item_accessories);");

    private static int CountArmorAndAccessories(SqliteConnection connection) => Count(connection,
        "SELECT (SELECT COUNT(*) FROM item_armors) + (SELECT COUNT(*) FROM item_accessories);");

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static uint ReadUInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : Convert.ToUInt32(reader.GetValue(index), CultureInfo.InvariantCulture);
    private static int ReadInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : Convert.ToInt32(reader.GetValue(index), CultureInfo.InvariantCulture);

    private static string FriendlyWeaponType(string name, string code)
    {
        if (name.Equals("fist", StringComparison.OrdinalIgnoreCase)) return "Unarmed (fists)";
        if (string.IsNullOrWhiteSpace(code)) return string.IsNullOrWhiteSpace(name) ? "Weapon type" : name;
        if (code.Equals("slow_blunt_staff", StringComparison.OrdinalIgnoreCase)) return "Scepter";
        if (code.Equals("fast_blunt_staff", StringComparison.OrdinalIgnoreCase)) return "Fast scepter";
        return string.Join(' ', code.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Equals("n", StringComparison.OrdinalIgnoreCase))
            .Select(word => word.ToLowerInvariant() switch
            {
                "1h" => "One-handed",
                "2h" => "Two-handed",
                "nor" => "Normal",
                "fst" => "Fast",
                "sod" => "Sword",
                "spell" => "Magic",
                "heal" => "Healing",
                _ => char.ToUpperInvariant(word[0]) + word[1..]
            }));
    }

    private static string ArmorTypeName(uint typeId) => typeId switch
    {
        1 => "Cloth armor",
        2 => "Leather armor",
        3 => "Plate armor",
        4 => "Pet armor",
        5 => "Other armor",
        _ => "Other armor class"
    };

    private static string SlotName(uint slotTypeId) => slotTypeId switch
    {
        1 => "Head",
        2 => "Neck",
        3 => "Chest",
        4 => "Waist",
        5 => "Legs",
        6 => "Hands",
        7 => "Feet",
        8 => "Arms",
        9 => "Back",
        10 => "Ear",
        11 => "Finger",
        12 => "Undershirt",
        13 => "Underpants",
        31 => "Cosplay",
        _ => "Other equipment slot"
    };

    private static string EnglishGradeName(string rawName, uint id) => rawName switch
    {
        "저급" => "Crude",
        "일반" => "Basic",
        "고급" => "Grand",
        "희귀" => "Rare",
        "고대" => "Arcane",
        "영웅" => "Heroic",
        "유일" => "Unique",
        "유물" => "Celestial",
        "경이" => "Divine",
        "서사" => "Epic",
        "전설" => "Legendary",
        "신화" => "Mythic",
        _ when !string.IsNullOrWhiteSpace(rawName) && rawName.All(character => character <= 127) => rawName,
        _ => GradeName(id)
    };

    private static string GradeName(uint id) => id switch
    {
        0 => "Crude",
        1 => "Basic",
        2 => "Grand",
        3 => "Rare",
        4 => "Arcane",
        5 => "Heroic",
        6 => "Unique",
        7 => "Celestial",
        8 => "Divine",
        9 => "Epic",
        10 => "Legendary",
        11 => "Mythic",
        _ => "Other item quality"
    };
}
