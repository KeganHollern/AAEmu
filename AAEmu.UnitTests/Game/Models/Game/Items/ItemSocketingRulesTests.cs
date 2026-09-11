using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

public sealed class ItemSocketingRulesTests
{
    [Test]
    public async Task SlotAndGrade_All372CompactCountRows_RespectTheExactBoundary()
    {
        var rules = LoadRules();
        for (uint slot = 1; slot <= 31; slot++)
        {
            var counts = Counts(slot);
            for (byte grade = 0; grade < 12; grade++)
            {
                var item = Equipment(slot, grade);
                await Assert.That(rules.GetSocketLimit(item)).IsEqualTo(counts[grade]);
                for (var count = 0; count <= 7; count++)
                {
                    item.GemIds = Enumerable.Repeat(30907u, count).Concat(Enumerable.Repeat(0u, 7 - count)).ToArray();
                    var expected = counts[grade] == 0 ? ErrorMessageType.InvalidTarget :
                        count >= counts[grade] ? ErrorMessageType.ItemSocketsFull : ErrorMessageType.NoErrorMessage;
                    // Fixture gem 1 has native group 0 and level 0, so only the count gate applies.
                    await Assert.That(rules.Validate(item, 1, out var actualCount)).IsEqualTo(expected);
                    await Assert.That(actualCount).IsEqualTo(count);
                }
            }
        }
    }

    [Test]
    [Arguments(14, 30907, true)]
    [Arguments(17, 30907, true)]
    [Arguments(18, 30907, true)]
    [Arguments(21, 30907, true)]
    [Arguments(16, 30907, false)]
    [Arguments(3, 30974, true)]
    [Arguments(5, 30974, false)]
    [Arguments(5, 32582, true)]
    [Arguments(3, 32582, false)]
    public async Task GemGroup_UsesAuthoredMembership(int slot, int gemId, bool allowed)
    {
        var result = LoadRules().Validate(Equipment((uint)slot), (uint)gemId, out _);
        await Assert.That(result).IsEqualTo(allowed ? ErrorMessageType.NoErrorMessage : ErrorMessageType.InvalidTarget);
    }

    [Test]
    [Arguments(19, 50, 30907, false)]
    [Arguments(20, 0, 30907, true)]
    [Arguments(50, 39, 30918, false)]
    [Arguments(20, 40, 30918, true)]
    [Arguments(60, 49, 30929, false)]
    [Arguments(20, 50, 30929, true)]
    public async Task Level_SeparatesItemLevelAndRequiredEquipLevel(int itemLevel, int requiredLevel, int gemId, bool allowed)
    {
        var item = Equipment(14);
        item.Template.Level = itemLevel;
        item.Template.LevelRequirement = requiredLevel;
        var result = LoadRules().Validate(item, (uint)gemId, out _);
        await Assert.That(result).IsEqualTo(allowed ? ErrorMessageType.NoErrorMessage : ErrorMessageType.SocketTargetLevel);
    }

    [Test]
    public async Task NativeDefaults_MissingRulesDenyAndUnrestrictedGroupsAllow()
    {
        var rules = LoadRules();
        var item = Equipment(14);
        await Assert.That(rules.Validate(item, 99999, out _)).IsEqualTo(ErrorMessageType.InvalidTarget);
        await Assert.That(rules.Validate(item, 29890, out _)).IsEqualTo(ErrorMessageType.SocketTargetLevel);
        await Assert.That(rules.Validate(item, 2, out _)).IsEqualTo(ErrorMessageType.InvalidTarget);
        await Assert.That(rules.Validate(item, 3, out _)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        item.Grade = 12;
        await Assert.That(rules.GetSocketLimit(item)).IsEqualTo(0);
        item.Grade = 5;
        item.Template = new EquipItemTemplate { Level = 50, LevelRequirement = 50 };
        await Assert.That(rules.GetSocketLimit(item)).IsEqualTo(0);
        await Assert.That(new ItemSocketingRules().Validate(Equipment(14), 30907, out _)).IsEqualTo(ErrorMessageType.SocketTargetLevel);
    }

    [Test]
    public async Task Dawnstone_RequiresGemsButNotAnInstallGradeOrGroup()
    {
        var rules = LoadRules();
        var item = Equipment(9, 0);
        await Assert.That(rules.Validate(item, Item.DawnStone, out _)).IsEqualTo(ErrorMessageType.ItemSocketsEmpty);
        item.GemIds[0] = 30907;
        await Assert.That(rules.Validate(item, Item.DawnStone, out var count)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task TemplateKinds_UseEquipmentSlotsNotInventoryPositions()
    {
        var rules = LoadRules();
        var item = Equipment(14);
        item.SlotType = SlotType.Bank;
        item.Slot = 3;
        await Assert.That(rules.Validate(item, 30907, out _)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        item.Template = new ArmorTemplate { Level = 50, LevelRequirement = 50, WearableTemplate = new Wearable { SlotTypeId = 3 } };
        await Assert.That(rules.Validate(item, 30974, out _)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        item.Template = new AccessoryTemplate { Level = 50, LevelRequirement = 50, WearableTemplate = new Wearable { SlotTypeId = 10 } };
        await Assert.That(rules.Validate(item, 1, out _)).IsEqualTo(ErrorMessageType.InvalidTarget);
        item.GemIds = new uint[8];
        await Assert.That(rules.Validate(item, 1, out _)).IsEqualTo(ErrorMessageType.InvalidTarget);
    }

    [Test]
    [Arguments("DELETE FROM content_configs")]
    [Arguments("UPDATE item_socket_num_limits SET num_socket=8 WHERE slot_id=14 AND grade_id=5")]
    public void Loader_InvalidCapacityOrMissingMinimum_StopsLoad(string change)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = change;
        command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => ItemSocketingRules.Load(connection));
    }

    internal static EquipItem Equipment(uint slot = 14, byte grade = 5) => new()
    {
        Grade = grade, Count = 1,
        Template = new WeaponTemplate { Id = 100, Level = 50, LevelRequirement = 50, HoldableTemplate = new Holdable { SlotTypeId = slot } }
    };

    internal static ItemSocketingRules LoadRules()
    {
        using var connection = CreateConnection();
        return ItemSocketingRules.Load(connection);
    }

    // The r208022 count rows. Grade IDs are not grade_order (common and poor differ).
    private static int[] Counts(uint slot) => slot switch
    {
        1 or 5 => [0, 0, 1, 1, 2, 3, 4, 5, 6, 6, 6, 6],
        3 or 14 or 16 or 17 or 18 or 20 or 21 => [0, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7],
        4 or 8 => [0, 0, 1, 1, 1, 2, 2, 3, 4, 4, 4, 4],
        6 or 7 => [0, 0, 1, 1, 1, 2, 3, 4, 5, 5, 5, 5],
        _ => new int[12]
    };

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE content_configs (id INT, kind_id INT, value INT);
            INSERT INTO content_configs VALUES (62,24,20);
            CREATE TABLE item_socket_num_limits (slot_id INT, grade_id INT, num_socket INT);
            CREATE TABLE equip_slot_groups (id INT, name TEXT);
            INSERT INTO equip_slot_groups VALUES (11,'weapons'),(23,'chest'),(24,'legs'),(99,'empty');
            CREATE TABLE equip_slot_group_maps (equip_slot_group_id INT, equip_slot_type_id INT);
            INSERT INTO equip_slot_group_maps VALUES (11,14),(11,17),(11,18),(11,21),(23,3),(24,5),(9,14);
            CREATE TABLE item_sockets (item_id INT, equip_slot_group_id INT);
            INSERT INTO item_sockets VALUES (30907,11),(30918,11),(30929,11),(30974,23),(32582,24),(29890,NULL),(1,NULL),(2,9),(3,99);
            CREATE TABLE item_socket_level_limits (item_id INT, level INT);
            INSERT INTO item_socket_level_limits VALUES (30907,0),(30918,40),(30929,50),(30974,0),(32582,0),(1,0),(2,0),(3,0);
            """;
        command.ExecuteNonQuery();
        for (uint slot = 1; slot <= 31; slot++)
            for (var grade = 0; grade < 12; grade++)
            {
                command.CommandText = $"INSERT INTO item_socket_num_limits VALUES ({slot},{grade},{Counts(slot)[grade]})";
                command.ExecuteNonQuery();
            }
        return connection;
    }
}
