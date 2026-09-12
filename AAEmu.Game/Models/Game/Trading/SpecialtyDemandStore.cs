using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Trading;

public static class SpecialtyDemandStore
{
    public static List<SpecialtyDemand> Load(MySqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, zone_group_id, ratio, pending_sales, consume_at, regenerate_at FROM specialty_demand";
        using var reader = command.ExecuteReader();
        var result = new List<SpecialtyDemand>();
        while (reader.Read())
        {
            result.Add(new SpecialtyDemand(reader.GetUInt32("item_id"), reader.GetUInt32("zone_group_id"),
                reader.GetDecimal("ratio"), reader.GetInt32("pending_sales"),
                DateTime.SpecifyKind(reader.GetDateTime("consume_at"), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime("regenerate_at"), DateTimeKind.Utc)));
        }
        return result;
    }

    public static void Save(MySqlConnection connection, MySqlTransaction transaction, SpecialtyDemand demand)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO specialty_demand " +
            "(item_id, zone_group_id, ratio, pending_sales, consume_at, regenerate_at) " +
            "VALUES (@item, @zone, @ratio, @pending, @consume, @regen) " +
            "ON DUPLICATE KEY UPDATE ratio=@ratio, pending_sales=@pending, consume_at=@consume, regenerate_at=@regen";
        command.Parameters.AddWithValue("@item", demand.ItemId);
        command.Parameters.AddWithValue("@zone", demand.ZoneGroupId);
        command.Parameters.AddWithValue("@ratio", demand.Ratio);
        command.Parameters.AddWithValue("@pending", demand.PendingSales);
        command.Parameters.AddWithValue("@consume", demand.ConsumeAt);
        command.Parameters.AddWithValue("@regen", demand.RegenerateAt);
        command.ExecuteNonQuery();
    }
}
