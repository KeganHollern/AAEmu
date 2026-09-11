using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

public sealed class ItemSocketingCompactTests
{
    [Test]
    [Explicit]
    public async Task ActiveR208022Compact_LoadsEveryCountRowAndAllLiveGems()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_SOCKET_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_SOCKET_TEST_COMPACT to the read-only r208022 server compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var rules = ItemSocketingRules.Load(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slot_id,grade_id,num_socket FROM item_socket_num_limits";
        var countRows = 0;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var item = ItemSocketingRulesTests.Equipment((uint)reader.GetInt32(0), (byte)reader.GetInt32(1));
                await Assert.That(rules.GetSocketLimit(item)).IsEqualTo(reader.GetInt32(2));
                countRows++;
            }
        await Assert.That(countRows).IsEqualTo(372);
        command.CommandText = """
            SELECT s.item_id,l.level,m.equip_slot_type_id
            FROM item_sockets s JOIN items i ON i.id=s.item_id
            JOIN item_socket_level_limits l ON l.item_id=s.item_id
            JOIN equip_slot_groups g ON g.id=s.equip_slot_group_id
            JOIN equip_slot_group_maps m ON m.equip_slot_group_id=g.id
            """;
        var liveGems = new HashSet<uint>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var gem = (uint)reader.GetInt32(0);
                var item = ItemSocketingRulesTests.Equipment((uint)reader.GetInt32(2));
                item.Template.LevelRequirement = reader.GetInt32(1);
                await Assert.That(rules.Validate(item, gem, out _)).IsEqualTo(ErrorMessageType.NoErrorMessage);
                liveGems.Add(gem);
            }
        await Assert.That(liveGems.Count).IsEqualTo(108);
    }
}
