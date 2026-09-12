using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Dominions;

public static class DominionStateStore
{
    public static Dictionary<ushort, DominionState> Load(MySqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT zone_group_id, siege_zone_id, owner_expedition_id, tax_rate, house_tax_balance FROM dominion_states";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<ushort, DominionState>();
        while (reader.Read())
        {
            var state = new DominionState(reader.GetUInt16("zone_group_id"), reader.GetUInt32("siege_zone_id"),
                reader.GetUInt32("owner_expedition_id"), reader.GetInt32("tax_rate"), reader.GetInt64("house_tax_balance"));
            result.Add(state.ZoneGroupId, state);
        }
        return result;
    }

    public static void Save(MySqlConnection connection, MySqlTransaction transaction, DominionState state)
    {
        // Validate the supported packet and state range before any write.
        _ = state.ToPacketData();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dominion_states (zone_group_id, siege_zone_id, owner_expedition_id, tax_rate, house_tax_balance)
            VALUES (@zone, @siege, @owner, @rate, @balance)
            ON DUPLICATE KEY UPDATE siege_zone_id=@siege, owner_expedition_id=@owner, tax_rate=@rate, house_tax_balance=@balance
            """;
        command.Parameters.AddWithValue("@zone", state.ZoneGroupId);
        command.Parameters.AddWithValue("@siege", state.SiegeZoneId);
        command.Parameters.AddWithValue("@owner", state.OwnerExpeditionId);
        command.Parameters.AddWithValue("@rate", state.TaxRate);
        command.Parameters.AddWithValue("@balance", state.HouseTaxBalance);
        command.ExecuteNonQuery();
    }
}
