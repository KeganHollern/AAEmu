using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class FishConversionMappingTests
{
    private static readonly FieldInfo s_lootInstance = typeof(Singleton<LootGameData>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private LootGameData _previousLoot;
    private ItemManager _items;
    private Dictionary<uint, ItemTemplate> _templates;
    private Dictionary<uint, LootPack> _packs;

    [Before(Test)]
    public void SetUp()
    {
        _previousLoot = (LootGameData)s_lootInstance.GetValue(null);
        var gameData = new LootGameData();
        _packs = [];
        SetField(gameData, "_lootPacks", _packs);
        s_lootInstance.SetValue(null, gameData);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        _templates = [];
        SetField(_items, "_templates", _templates);
    }

    [After(Test)]
    public void TearDown()
    {
        s_lootInstance.SetValue(null, _previousLoot);
    }

    [Test]
    [MethodDataSource(nameof(DeployedConversions))]
    public async Task EveryDeployedConversion_ResolvesItsFunctionAndExactTrophy(
        uint functionId, uint input, uint packId, uint lootId, uint output)
    {
        foreach (var row in DeployedConversions())
            AddConversion(row.Item1, row.Item2, row.Item3, row.Item4, row.Item5);

        var result = _items.TryGetFishConversion(functionId, input, out var loot);

        await Assert.That(result).IsTrue();
        await Assert.That(loot.LootPackId).IsEqualTo(packId);
        await Assert.That(loot.Id).IsEqualTo(lootId);
        await Assert.That(loot.ItemId).IsEqualTo(output);
        await Assert.That(loot.MinAmount).IsEqualTo(1);
        await Assert.That(loot.MaxAmount).IsEqualTo(1);
    }

    [Test]
    public async Task SameInputAcrossFunctions_ResolvesEachFunctionsOwnPack()
    {
        AddConversion(1, 100, 10, 1, 101);
        AddConversion(2, 100, 20, 2, 102);

        await Assert.That(_items.TryGetFishConversion(1, 100, out var first)).IsTrue();
        await Assert.That(_items.TryGetFishConversion(2, 100, out var second)).IsTrue();
        await Assert.That(first.ItemId).IsEqualTo(101u);
        await Assert.That(second.ItemId).IsEqualTo(102u);
    }

    [Test]
    [Arguments(1u, 999u)]
    [Arguments(999u, 100u)]
    [Arguments(0u, 100u)]
    [Arguments(1u, 0u)]
    public async Task MissingExactMapping_DoesNotFallBack(uint functionId, uint input)
    {
        AddConversion(1, 100, 10, 1, 101);

        await Assert.That(_items.TryGetFishConversion(functionId, input, out var loot)).IsFalse();
        await Assert.That(loot).IsNull();
    }

    [Test]
    [Arguments(10u)]
    [Arguments(20u)]
    public void DuplicateExactKey_RejectsIdenticalAndConflictingPackIds(uint duplicatePackId)
    {
        AddConversion(1, 100, 10, 1, 101);

        Assert.Throws<InvalidDataException>(() => _items.AddFishConversion(new LootPackConvertFish
        {
            Id = 2, DoodadFuncConvertFishId = 1, ItemId = 100, LootPackId = duplicatePackId
        }));
    }

    [Test]
    [Arguments(0u, 100u, 10u)]
    [Arguments(1u, 0u, 10u)]
    [Arguments(1u, 100u, 0u)]
    public void InvalidAssociationIdentifiers_RejectAtLoad(uint functionId, uint input, uint packId)
    {
        Assert.Throws<InvalidDataException>(() => _items.AddFishConversion(new LootPackConvertFish
        {
            Id = 1, DoodadFuncConvertFishId = functionId, ItemId = input, LootPackId = packId
        }));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MissingOrAmbiguousLootPack_RejectsBeforeAnyItemAllocation(int invalidPack)
    {
        AddConversion(1, 100, 10, 1, 101);
        switch (invalidPack)
        {
            case 0: _packs.Clear(); break;
            case 1: _packs[10] = new LootPack { Id = 10, Loots = null }; break;
            case 2: _packs[10].Loots.Clear(); break;
            case 3: _packs[10].Loots.Add(new Loot { ItemId = 102 }); break;
        }

        await Assert.That(_items.TryGetFishConversion(1, 100, out var loot)).IsFalse();
        await Assert.That(loot).IsNull();
    }

    [Test]
    [Arguments(0u, 1, 1)]
    [Arguments(9_999_999u, 1, 1)]
    [Arguments(10_000_001u, 1, 1)]
    [Arguments(10_000_000u, 0, 1)]
    [Arguments(10_000_000u, 1, 2)]
    [Arguments(10_000_000u, 2, 1)]
    [Arguments(10_000_000u, 2, 2)]
    public async Task UnsupportedOutput_RejectsWithoutInventingRandomSemantics(uint rate, int min, int max)
    {
        AddConversion(1, 100, 10, 1, 101);
        var candidate = _packs[10].Loots[0];
        candidate.DropRate = rate;
        candidate.MinAmount = min;
        candidate.MaxAmount = max;

        await Assert.That(_items.TryGetFishConversion(1, 100, out var loot)).IsFalse();
        await Assert.That(loot).IsNull();
    }

    [Test]
    [Arguments(100u)]
    [Arguments(101u)]
    public async Task MissingInputOrOutputTemplate_Rejects(uint missingTemplateId)
    {
        AddConversion(1, 100, 10, 1, 101);
        _templates.Remove(missingTemplateId);

        await Assert.That(_items.TryGetFishConversion(1, 100, out var loot)).IsFalse();
        await Assert.That(loot).IsNull();
    }

    [Test]
    public async Task DeployedData_CoversAllThreeFunctionsAndTwentyEightPacks()
    {
        var rows = DeployedConversions().ToArray();

        await Assert.That(rows.Length).IsEqualTo(54);
        await Assert.That(rows.Count(row => row.Item1 == 1)).IsEqualTo(1);
        await Assert.That(rows.Count(row => row.Item1 == 2)).IsEqualTo(28);
        await Assert.That(rows.Count(row => row.Item1 == 4)).IsEqualTo(25);
        await Assert.That(rows.Select(row => row.Item3).Distinct().Count()).IsEqualTo(28);
        await Assert.That(rows.Select(row => (row.Item1, row.Item2)).Distinct().Count()).IsEqualTo(54);
    }

    private void AddConversion(uint functionId, uint input, uint packId, uint lootId, uint output)
    {
        _items.AddFishConversion(new LootPackConvertFish
        {
            Id = lootId, DoodadFuncConvertFishId = functionId, ItemId = input, LootPackId = packId
        });
        _templates[input] = new ItemTemplate { Id = input };
        _templates[output] = new ItemTemplate { Id = output };
        _packs.TryAdd(packId, new LootPack
        {
            Id = packId,
            Loots = [new Loot
            {
                Id = lootId, LootPackId = packId, Group = 1, ItemId = output,
                DropRate = 10_000_000, MinAmount = 1, MaxAmount = 1
            }]
        });
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    // Exact 54 active-header associations joined to loots from r208022 compact snapshots:
    // server SHA-256 636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac
    // client SHA-256 4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4
    // SELECT c.doodad_func_convert_fish_id,c.item_id,c.loot_pack_id,l.id,l.item_id
    // FROM doodad_func_convert_fish_items c JOIN doodad_func_convert_fishes f
    // ON f.id=c.doodad_func_convert_fish_id JOIN loots l ON l.loot_pack_id=c.loot_pack_id
    // ORDER BY c.doodad_func_convert_fish_id,c.item_id,l.id;
    public static IEnumerable<(uint, uint, uint, uint, uint)> DeployedConversions()
    {
        yield return (1u, 27457u, 7857u, 72713u, 28005u);
        yield return (2u, 27118u, 7897u, 72784u, 28315u);
        yield return (2u, 27457u, 7857u, 72713u, 28005u);
        yield return (2u, 27458u, 7858u, 72719u, 28032u);
        yield return (2u, 27501u, 7859u, 72720u, 28033u);
        yield return (2u, 27502u, 7860u, 72721u, 28044u);
        yield return (2u, 27503u, 7861u, 72722u, 28045u);
        yield return (2u, 27504u, 7862u, 72723u, 28046u);
        yield return (2u, 27599u, 7863u, 72714u, 27921u);
        yield return (2u, 27600u, 7864u, 72717u, 28028u);
        yield return (2u, 27601u, 7865u, 72718u, 28029u);
        yield return (2u, 27602u, 7866u, 72715u, 28004u);
        yield return (2u, 27603u, 7867u, 72724u, 28030u);
        yield return (2u, 27604u, 7868u, 72725u, 28031u);
        yield return (2u, 27605u, 7869u, 72716u, 28006u);
        yield return (2u, 27606u, 7870u, 72726u, 28034u);
        yield return (2u, 27607u, 7871u, 72727u, 28035u);
        yield return (2u, 27608u, 7872u, 72728u, 28041u);
        yield return (2u, 27609u, 7873u, 72729u, 28042u);
        yield return (2u, 27610u, 7874u, 72730u, 28043u);
        yield return (2u, 27611u, 7875u, 72731u, 28047u);
        yield return (2u, 27612u, 7876u, 72732u, 28048u);
        yield return (2u, 27613u, 7877u, 72733u, 28049u);
        yield return (2u, 28321u, 8011u, 73317u, 29012u);
        yield return (2u, 28322u, 8012u, 73318u, 29013u);
        yield return (2u, 30422u, 8286u, 74032u, 30430u);
        yield return (2u, 30428u, 8285u, 74030u, 30431u);
        yield return (2u, 30429u, 8284u, 74029u, 30432u);
        yield return (2u, 31691u, 8356u, 74890u, 31692u);
        yield return (4u, 27457u, 7857u, 72713u, 28005u);
        yield return (4u, 27458u, 7858u, 72719u, 28032u);
        yield return (4u, 27501u, 7859u, 72720u, 28033u);
        yield return (4u, 27502u, 7860u, 72721u, 28044u);
        yield return (4u, 27503u, 7861u, 72722u, 28045u);
        yield return (4u, 27504u, 7862u, 72723u, 28046u);
        yield return (4u, 27599u, 7863u, 72714u, 27921u);
        yield return (4u, 27600u, 7864u, 72717u, 28028u);
        yield return (4u, 27601u, 7865u, 72718u, 28029u);
        yield return (4u, 27602u, 7866u, 72715u, 28004u);
        yield return (4u, 27603u, 7867u, 72724u, 28030u);
        yield return (4u, 27604u, 7868u, 72725u, 28031u);
        yield return (4u, 27605u, 7869u, 72716u, 28006u);
        yield return (4u, 27606u, 7870u, 72726u, 28034u);
        yield return (4u, 27607u, 7871u, 72727u, 28035u);
        yield return (4u, 27608u, 7872u, 72728u, 28041u);
        yield return (4u, 27609u, 7873u, 72729u, 28042u);
        yield return (4u, 27610u, 7874u, 72730u, 28043u);
        yield return (4u, 27611u, 7875u, 72731u, 28047u);
        yield return (4u, 27612u, 7876u, 72732u, 28048u);
        yield return (4u, 27613u, 7877u, 72733u, 28049u);
        yield return (4u, 30422u, 8286u, 74032u, 30430u);
        yield return (4u, 30428u, 8285u, 74030u, 30431u);
        yield return (4u, 30429u, 8284u, 74029u, 30432u);
        yield return (4u, 31691u, 8356u, 74890u, 31692u);
    }
}
