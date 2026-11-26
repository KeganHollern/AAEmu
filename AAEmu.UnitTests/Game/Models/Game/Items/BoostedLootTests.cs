using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class BoostedLootTests
{
    private static readonly FieldInfo ItemInstance = typeof(Singleton<ItemManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly Dictionary<uint, ItemTemplate> _items = [];
    private object _previousItems;
    private WorldConfig _previousWorld;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        _previousItems = ItemInstance.GetValue(null);
        var manager = new ItemManager(null, null, null, null, null, null);
        typeof(ItemManager).GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, _items);
        ItemInstance.SetValue(null, manager);
    }

    [After(Test)]
    public void Restore()
    {
        ItemInstance.SetValue(null, _previousItems);
        AppConfiguration.Instance.World = _previousWorld;
    }

    [Test]
    public async Task BoostUsesOnlyRowsThatPassTheActabilityFilter()
    {
        var pack = Pack(3);
        pack.ActabilityGroups[1] = new LootActabilityGroups { GroupId = 1, MaxDice = 5000 };
        var rolls = new Rolls { Index = 1, Actability = new Queue<int>([100, 9999, 100]) };
        var reward = pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, 5, rolls).Single();
        await Assert.That(rolls.Width).IsEqualTo(2);
        await Assert.That(reward).IsEqualTo((12u, 1, (byte)5, 1u));
        var allFiltered = new Rolls { Actability = new Queue<int>([9999, 9999, 9999]) };
        await Assert.That(pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, allFiltered).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("guaranteed")]
    [Arguments("mixed weight")]
    [Arguments("coin")]
    [Arguments("singleton")]
    [Arguments("quantity")]
    [Arguments("grade")]
    [Arguments("quest")]
    [Arguments("missing item")]
    public async Task OtherGroupsKeepTheirWeightedChoice(string kind)
    {
        var pack = Pack(kind == "singleton" ? 1 : 2);
        var last = pack.LootsByGroupNo[1][^1];
        switch (kind)
        {
            case "guaranteed": last.AlwaysDrop = true; break;
            case "mixed weight": pack.LootsByGroupNo[1][1] = new Loot { Group = 1, ItemId = 11, DropRate = 9_000_000 }; break;
            case "coin": last.ItemId = 500; _items[500] = new ItemTemplate { Id = 500 }; break;
            case "quantity": last.MaxAmount++; break;
            case "grade": last.GradeId++; break;
            case "quest": _items[last.ItemId].LootQuestId = 123; break;
            case "missing item": _items.Remove(last.ItemId); break;
        }
        var rolls = new Rolls();
        var result = pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, rolls);
        await Assert.That(result.Single().itemId).IsEqualTo(10u);
        await Assert.That(rolls.WeightedRolls).IsEqualTo(1);
        await Assert.That(rolls.Width).IsEqualTo(0);
    }

    [Test]
    [Arguments(0f, 0)]
    [Arguments(0.5f, 0)]
    [Arguments(1f, 1)]
    public async Task ZeroAndUnboostedRatesKeepTheirAwardCount(float rate, int awards)
    {
        var rolls = new Rolls { Fraction = 0.9f };
        var result = Pack(7).GeneratePackNewV2(rate, 1, null, ActabilityType.None, null, rolls);
        await Assert.That(result.Count).IsEqualTo(awards);
        await Assert.That(rolls.Width).IsEqualTo(0);
        if (awards > 0)
            await Assert.That(result[0].itemId).IsEqualTo(16u);
    }

    [Test]
    [Arguments(1.5, 1f, 3)]
    [Arguments(0.5, 2f, 0)]
    public async Task EffectiveBoostIncludesTheWorldRate(double worldRate, float playerRate, int expectedWidth)
    {
        AppConfiguration.Instance.World.LootRate = worldRate;
        var rolls = new Rolls();
        var result = Pack(3).GeneratePackNewV2(playerRate, 1, null, ActabilityType.None, null, rolls);
        await Assert.That(rolls.Width).IsEqualTo(expectedWidth);
        await Assert.That(result.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EmptyOrZeroWeightGroupsDoNotGainAnAward()
    {
        foreach (var count in new[] { 0, 2 })
        {
            var pack = Pack(count);
            foreach (var row in pack.LootsByGroupNo[1])
                row.DropRate = 0;
            var rolls = new Rolls();
            var rewards = pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, rolls);
            await Assert.That(rewards.Count).IsEqualTo(0);
            await Assert.That(rolls.Width).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GroupZeroStillRollsEachItem()
    {
        var pack = Pack(2);
        var rows = pack.LootsByGroupNo[1];
        pack.LootsByGroupNo.Clear();
        pack.LootsByGroupNo.Add(0, rows);
        foreach (var row in rows)
            row.Group = 0;
        var rolls = new Rolls();
        var result = pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, rolls);
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(rolls.Width).IsEqualTo(0);
    }

    [Test]
    public async Task BoostKeepsTheGroupGateAndAwardsOneItemAtEveryIndex()
    {
        var pack = Pack(42);
        _items[51].FixedGrade = 4; // The item factory applies fixed grades after the selection.
        pack.Groups[1] = new LootGroups { GroupNo = 1, DropRate = 500_000 };
        for (var index = 0; index < 42; index++)
        {
            var rolls = new Rolls { Index = index, Gate = 549_999 };
            var award = pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, rolls).Single();
            await Assert.That(award).IsEqualTo(((uint)(10 + index), 1, (byte)0, 1u));
            await Assert.That(rolls.Width).IsEqualTo(42);
        }
        var miss = pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, new Rolls { Gate = 550_000 });
        await Assert.That(miss.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StockPacksKeepAllChoicesAtEqualLoadedWeights()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_COMBAT_TEST_COMPACT to the r208022 server compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var data = new LootGameData();
        data.Load(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, COALESCE(loot_quest_id, 0), fixed_grade FROM items";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = (uint)reader.GetInt32(0);
            _items[id] = new ItemTemplate { Id = id, LootQuestId = (uint)reader.GetInt32(1), FixedGrade = reader.GetInt32(2) };
        }
        foreach (var (packId, group, count) in new[] { (6586u, 1u, 42), (6188u, 2u, 7), (6103u, 2u, 234), (6158u, 1u, 5), (6234u, 1u, 2) })
        {
            var pack = data.GetPack(packId);
            var authored = pack.LootsByGroupNo[group];
            await Assert.That(authored.Count).IsEqualTo(count);
            for (var index = 0; index < count; index++)
            {
                var rolls = new Rolls { Index = index };
                var award = pack.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, rolls).Single(x => x.lootGroupOrigin == group);
                await Assert.That(rolls.Width).IsEqualTo(count);
                await Assert.That(award.itemId).IsEqualTo(authored[index].ItemId);
            }
        }
        var logbook = data.GetPack(6234);
        var chapters = logbook.LootsByGroupNo[1];
        await Assert.That(chapters.All(row => row.DropRate == 5_000_000)).IsTrue();
        await Assert.That(logbook.Groups[1].DropRate).IsEqualTo(3_000_000u);
        var lastPassingRoll = new Rolls { Index = 1, Gate = 3_299_999 };
        var chapterThree = logbook.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, lastPassingRoll).Single();
        await Assert.That(chapterThree).IsEqualTo((18741u, 1, (byte)0, 1u));
        var blocked = logbook.GeneratePackNewV2(1.1f, 1, null, ActabilityType.None, null, new Rolls { Gate = 3_300_000 });
        await Assert.That(blocked.Count).IsEqualTo(0);
    }

    private LootPack Pack(int count)
    {
        var rows = new List<Loot>();
        for (uint id = 10; id < 10 + count; id++)
        {
            rows.Add(new Loot { Group = 1, ItemId = id, DropRate = 10_000_000, MinAmount = 1, MaxAmount = 1 });
            _items[id] = new ItemTemplate { Id = id };
        }
        return new LootPack { Groups = [], ActabilityGroups = [], LootsByGroupNo = new() { [1] = rows } };
    }

    private sealed class Rolls : Random
    {
        public int Gate { get; init; }
        public int Index { get; init; }
        public float Fraction { get; init; }
        public Queue<int> Actability { get; init; } = new();
        public int Width { get; private set; }
        public int WeightedRolls { get; private set; }

        public override int Next(int maxValue)
        {
            Width = maxValue;
            return Index;
        }

        public override int Next(int minValue, int maxValue) => (minValue, maxValue) switch
        {
            (0, 10_000_000) => Gate,
            (0, 10_000) => Actability.Dequeue(),
            _ => minValue
        };

        public override float NextSingle()
        {
            WeightedRolls++;
            return Fraction;
        }
    }
}
