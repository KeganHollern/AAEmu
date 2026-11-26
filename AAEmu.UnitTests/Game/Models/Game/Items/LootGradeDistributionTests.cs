using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class LootGradeDistributionTests
{
    private static readonly FieldInfo ItemInstance = typeof(Singleton<ItemManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly Dictionary<int, GradeDistributions> _grades = [];
    private readonly Dictionary<uint, ItemTemplate> _items = [];
    private WorldConfig _previousWorld;
    private object _previousItems;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        _previousItems = ItemInstance.GetValue(null);
        var manager = new ItemManager(null, null, null, null, null, null);
        typeof(ItemManager).GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, _items);
        typeof(ItemManager).GetField("_itemGradeDistributions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, _grades);
        ItemInstance.SetValue(null, manager);
    }

    [After(Test)]
    public void Restore()
    {
        ItemInstance.SetValue(null, _previousItems);
        AppConfiguration.Instance.World = _previousWorld;
    }

    [Test]
    public async Task DivineGardenGear_EveryGradeGetsItsAuthoredNumberOfRolls()
    {
        // Distribution 11 on the cloth, leather and plate packs has a one-percent final grade.
        _grades[11] = new GradeDistributions { Weight2 = 72, Weight3 = 24, Weight4 = 3, Weight5 = 1 };
        var loot = new Loot { Group = 1, ItemId = 26978, MinAmount = 1, MaxAmount = 1, DropRate = 10_000_000 };
        _items.Add(loot.ItemId, new ItemTemplate { Id = loot.ItemId, FixedGrade = -1 });
        var pack = new LootPack
        {
            Id = 7734, ActabilityGroups = [], LootsByGroupNo = new() { [1] = [loot] },
            Groups = new() { [1] = new LootGroups { GroupNo = 1, DropRate = 10_000_000, ItemGradeDistributionId = 11 } }
        };
        int[] observed = new int[12];
        for (var roll = 0; roll < 100; roll++)
        {
            var award = pack.GeneratePackNewV2(1, 1, null, ActabilityType.None, null, new GradeRoll(roll)).Single();
            observed[award.grade]++;
            await Assert.That((award.itemId, award.count, award.lootGroupOrigin)).IsEqualTo((26978u, 1, 1u));
        }
        int[] expected = [0, 0, 72, 24, 3, 1, 0, 0, 0, 0, 0, 0];
        for (var grade = 0; grade < expected.Length; grade++)
            await Assert.That(observed[grade]).IsEqualTo(expected[grade]);

        var inherited = pack.GeneratePackNewV2(1, 1, null, ActabilityType.None, 8, new GradeRoll(-1)).Single();
        await Assert.That(inherited.grade).IsEqualTo((byte)8);
    }

    [Test]
    public async Task ExactCompact_ValidatesEveryGradeRollAndTheDivineGardenClothRoute()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_COMBAT_TEST_COMPACT to the r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.item_grade_distribution_id
            FROM loot_pack_dropping_npcs n
            JOIN loots source ON source.loot_pack_id = n.loot_pack_id
            JOIN items i ON i.id = source.item_id
            JOIN skill_effects se ON se.skill_id = i.use_skill_id
            JOIN effects e ON e.id = se.effect_id AND e.actual_type = 'GainLootPackItemEffect'
            JOIN gain_loot_pack_item_effects p ON p.id = e.actual_id
            JOIN loot_groups g ON g.pack_id = p.loot_pack_id AND g.group_no = 1
            WHERE n.npc_id = 4331 AND n.loot_pack_id = 9335 AND i.id = 27468 AND i.use_skill_id = 21659
              AND p.loot_pack_id = 7734 AND p.inherit_grade = 'f'
            """;
        await Assert.That(Convert.ToInt32(command.ExecuteScalar())).IsEqualTo(11);
        var data = new LootGameData();
        data.Load(connection);
        var pack = data.GetPack(7734);
        command.CommandText = """
            SELECT i.id, i.fixed_grade FROM loots l JOIN items i ON i.id = l.item_id
            WHERE l.loot_pack_id = 7734
            """;
        using (var gear = command.ExecuteReader())
        {
            while (gear.Read())
            {
                var itemId = (uint)gear.GetInt32(0);
                _items.Add(itemId, new ItemTemplate { Id = itemId, FixedGrade = gear.GetInt32(1) });
            }
        }
        await Assert.That(_items.Count).IsEqualTo(21);
        await Assert.That(_items.Values.All(item => item.FixedGrade == -1)).IsTrue();

        command.CommandText = """
            SELECT id, weight_0, weight_1, weight_2, weight_3, weight_4, weight_5, weight_6,
                   weight_7, weight_8, weight_9, weight_10, weight_11 FROM item_grade_distributions
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetByte(0);
            var weights = Enumerable.Range(1, 12).Select(reader.GetInt32).ToArray();
            _grades[id] = new GradeDistributions
            {
                Id = id, Weight0 = weights[0], Weight1 = weights[1], Weight2 = weights[2], Weight3 = weights[3],
                Weight4 = weights[4], Weight5 = weights[5], Weight6 = weights[6], Weight7 = weights[7],
                Weight8 = weights[8], Weight9 = weights[9], Weight10 = weights[10], Weight11 = weights[11]
            };
            // Exercise all stock distributions with the same admitted gear group.
            pack.Groups[1].ItemGradeDistributionId = id;
            var roll = 0;
            for (byte grade = 0; grade < weights.Length; grade++)
            {
                for (var i = 0; i < weights[grade]; i++)
                {
                    var awards = pack.GeneratePackNewV2(1, 1, null, ActabilityType.None, null, new GradeRoll(roll++));
                    await Assert.That(awards.Single(x => x.lootGroupOrigin == 1).grade).IsEqualTo(grade);
                }
            }
            await Assert.That(roll).IsEqualTo(100);
        }
        await Assert.That(_grades.Count).IsEqualTo(25);
        pack.Groups[1].ItemGradeDistributionId = 11;
        var first = pack.GeneratePackNewV2(1, 1, null, ActabilityType.None, null, new GradeRoll(0)).Single(x => x.lootGroupOrigin == 1);
        var last = pack.GeneratePackNewV2(1, 1, null, ActabilityType.None, null, new GradeRoll(99)).Single(x => x.lootGroupOrigin == 1);
        await Assert.That((first.itemId, first.count, first.grade)).IsEqualTo((26978u, 1, (byte)2));
        await Assert.That((last.itemId, last.count, last.grade)).IsEqualTo((26978u, 1, (byte)5));
    }

    private sealed class GradeRoll(int grade) : Random
    {
        public override int Next(int minValue, int maxValue) => (minValue, maxValue) switch
        {
            (0, 100) when grade >= 0 => grade,
            (0, 100) => throw new InvalidOperationException("An inherited grade cannot roll a distribution."),
            _ => minValue
        };

        public override float NextSingle() => 0;
    }
}
