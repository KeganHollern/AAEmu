using AAEmu.Game.Models.Game.Items.Templates;
using Microsoft.Data.Sqlite;

namespace AAEmu.Game.Models.Game.Items;

/// <summary>The r208022 lunagem rules. Inventory positions are not equipment slot types.</summary>
public sealed class ItemSocketingRules
{
    private readonly Dictionary<(uint Slot, byte Grade), int> _counts = [];
    private readonly Dictionary<uint, uint> _gems = [];
    private readonly Dictionary<uint, int> _levels = [];
    private readonly Dictionary<uint, HashSet<uint>> _groups = [];
    private int _minimumItemLevel = int.MaxValue;

    public static ItemSocketingRules Load(SqliteConnection connection)
    {
        var rules = new ItemSocketingRules();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM content_configs WHERE id = 62";
        var minimum = command.ExecuteScalar();
        if (minimum == null || minimum == DBNull.Value || Convert.ToInt32(minimum) < 0)
            throw new InvalidDataException("Missing or invalid lunagem minimum item level (content_configs.id=62).");
        rules._minimumItemLevel = Convert.ToInt32(minimum);

        command.CommandText = "SELECT slot_id, grade_id, num_socket FROM item_socket_num_limits";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var count = reader.GetInt32(2);
                if (count is < 0 or > 7)
                    throw new InvalidDataException("Lunagem socket count exceeds the r208022 equipment detail capacity.");
                rules._counts.Add((checked((uint)reader.GetInt32(0)), checked((byte)reader.GetInt32(1))), count);
            }
        }

        command.CommandText = "SELECT id FROM equip_slot_groups";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rules._groups.Add(checked((uint)reader.GetInt32(0)), []);

        command.CommandText = "SELECT equip_slot_group_id, equip_slot_type_id FROM equip_slot_group_maps";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (rules._groups.TryGetValue(checked((uint)reader.GetInt32(0)), out var slots))
                    slots.Add(checked((uint)reader.GetInt32(1)));

        command.CommandText = "SELECT item_id, equip_slot_group_id FROM item_sockets";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rules._gems.Add(checked((uint)reader.GetInt32(0)), reader.IsDBNull(1) ? 0 : checked((uint)reader.GetInt32(1)));

        command.CommandText = "SELECT item_id, level FROM item_socket_level_limits";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rules._levels.Add(checked((uint)reader.GetInt32(0)), reader.GetInt32(1));
        return rules;
    }

    public int GetSocketLimit(EquipItem item)
    {
        if (item?.Template == null || item.Template.Level < _minimumItemLevel)
            return 0;
        return _counts.GetValueOrDefault((GetEquipmentSlot(item.Template), item.Grade));
    }

    public ErrorMessageType Validate(EquipItem item, uint sourceTemplateId, out int gemCount)
    {
        gemCount = 0;
        if (item?.Template == null || item.GemIds is not { Length: 7 })
            return ErrorMessageType.InvalidTarget;
        if (item.Template.Level < _minimumItemLevel)
            return ErrorMessageType.SocketTargetLevel;

        gemCount = item.GemIds.Count(id => id != 0);
        // Extraction does not need an install slot, grade, or gem level rule.
        if (sourceTemplateId == Item.DawnStone)
            return gemCount == 0 ? ErrorMessageType.ItemSocketsEmpty : ErrorMessageType.NoErrorMessage;

        if (!_gems.TryGetValue(sourceTemplateId, out var group))
            return ErrorMessageType.InvalidTarget;
        // Native lookup uses INT_MAX for a missing gem level row.
        if (!_levels.TryGetValue(sourceTemplateId, out var level) || level < 0 || item.Template.LevelRequirement < level)
            return ErrorMessageType.SocketTargetLevel;

        var slot = GetEquipmentSlot(item.Template);
        if (slot == 0 || !MatchesSlot(group, slot) || GetSocketLimit(item) == 0)
            return ErrorMessageType.InvalidTarget;
        return gemCount >= GetSocketLimit(item) ? ErrorMessageType.ItemSocketsFull : ErrorMessageType.NoErrorMessage;
    }

    private bool MatchesSlot(uint group, uint slot)
    {
        // Native group 0 and a defined empty group impose no slot restriction.
        // Unknown nonzero groups deny the target. The compact contains one orphan map.
        return group == 0 || _groups.TryGetValue(group, out var slots) && (slots.Count == 0 || slots.Contains(slot));
    }

    private static uint GetEquipmentSlot(ItemTemplate template) => template switch
    {
        WeaponTemplate weapon => weapon.HoldableTemplate?.SlotTypeId ?? 0,
        ArmorTemplate armor => armor.WearableTemplate?.SlotTypeId ?? 0,
        AccessoryTemplate accessory => accessory.WearableTemplate?.SlotTypeId ?? 0,
        _ => 0
    };
}
