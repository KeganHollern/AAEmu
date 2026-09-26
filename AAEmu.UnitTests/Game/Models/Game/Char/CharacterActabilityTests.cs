using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class CharacterActabilityTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private readonly Dictionary<int, ExpertLimit> _ranks = [];
    private readonly Dictionary<int, ExpandExpertLimit> _expansions = [];
    private readonly Dictionary<ulong, Item> _items = [];
    private CharacterMock _owner;
    private ItemTemplate _seal;

    [Before(Test)]
    public void SetUp()
    {
        var characters = new CharacterManager(null, null, null, null, null, null, null, null, null, null, null);
        SetField(characters, "_expertLimits", _ranks);
        SetField(characters, "_expandExpertLimits", _expansions);
        ReplaceSingleton(characters);
        int[] caps = [10000, 20000, 30000, 40000, 50000, 70000, 90000, 110000];
        byte[] counts = [0, 7, 6, 5, 4, 3, 2, 0];
        int[] reductions = [0, 5, 10, 15, 20, 23, 23, 23];
        for (var step = 0; step < caps.Length; step++)
            _ranks.Add(step, new ExpertLimit
            {
                Id = (uint)step + 1, UpLimit = caps[step], ExpertLimitCount = counts[step],
                Advantage = reductions[step], CastAdvantage = reductions[step], ExpMultiplier = step * 20, Show = step < 7
            });
        for (var step = 0; step < 14; step++)
            _expansions.Add(step, new ExpandExpertLimit { ExpandCount = (byte)(step + 1), ItemId = 29656, ItemCount = step + 1 });
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        ReplaceSingleton(items);
        SetField(items, "_allItems", _items);
        SetField(items, "_removedItems", new List<ulong>());
        ReplaceSingleton(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _owner = new CharacterMock { Id = 1, NumInventorySlots = 20, VocationPoint = 100 };
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>().Where(type => type != SlotType.EquipmentMate))
        {
            var container = new ItemContainer(1, type, false, _owner) { Owner = _owner, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        SetField(items, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
        _owner.Actability = new CharacterActability(_owner);
        _seal = new ItemTemplate { Id = 29656, MaxCount = 100, BindType = ItemBindType.Normal };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousInstances)
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments((byte)0, 1f, 1f)]
    [Arguments((byte)1, 0.95f, 1.2f)]
    [Arguments((byte)2, 0.90f, 1.4f)]
    [Arguments((byte)3, 0.85f, 1.6f)]
    [Arguments((byte)4, 0.80f, 1.8f)]
    [Arguments((byte)5, 0.77f, 2f)]
    [Arguments((byte)6, 0.77f, 2.2f)]
    public async Task Rank_UsesAuthoredPercentagesWithoutChangingLoot(byte step, float cost, float xp)
    {
        var ability = AddAbility(1, step);
        await Assert.That(ability.GetLaborCostMultiplier()).IsEqualTo(cost);
        await Assert.That(ability.GetProductionTimeMultiplier()).IsEqualTo(cost);
        await Assert.That(ability.GetExpMultiplier()).IsEqualTo(xp);
        await Assert.That(ability.GetLootMultiplier()).IsEqualTo(1f);
        var skill = new Skill(new SkillTemplate { ConsumeLaborPower = 100, ActabilityGroupId = 1 });
        await Assert.That(skill.GetLaborCost(_owner)).IsEqualTo((int)(100 * cost));
    }

    [Test]
    public async Task Rank_UsesSeparateCastAndLaborFieldsAtTheUnlockedRank()
    {
        _ranks[1].Advantage = 7;
        _ranks[1].CastAdvantage = 13;
        _ranks[1].ExpMultiplier = 35;
        var ability = AddAbility(1, 1, 20000);
        await Assert.That(ability.GetLaborCostMultiplier()).IsEqualTo(0.93f);
        await Assert.That(ability.GetProductionTimeMultiplier()).IsEqualTo(0.87f);
        await Assert.That(ability.GetExpMultiplier()).IsEqualTo(1.35f);
        await Assert.That(ability.Step).IsEqualTo((byte)1);
    }

    [Test]
    [Arguments((byte)1, 7)]
    [Arguments((byte)2, 6)]
    [Arguments((byte)3, 5)]
    [Arguments((byte)4, 4)]
    [Arguments((byte)5, 3)]
    [Arguments((byte)6, 2)]
    public async Task Regrade_CountsEveryRankAtOrAboveTheRequestedRank(byte next, int limit)
    {
        var target = AddAbility(1, (byte)(next - 1), _ranks[next - 1].UpLimit);
        for (uint id = 2; id <= limit + 1; id++)
            AddAbility(id, (byte)(id % 2 == 0 ? next : 6));
        _owner.Actability.Regrade(1, true);
        await Assert.That(target.Step).IsEqualTo((byte)(next - 1));
        _owner.ExpandedExpert = 1;
        _owner.Actability.Regrade(1, true);
        await Assert.That(target.Step).IsEqualTo(next);
        await Assert.That(target.Point).IsEqualTo(_ranks[next - 1].UpLimit);
    }

    [Test]
    public async Task Regrade_RejectsInsufficientPointsHiddenRanksAndUnknownIdentifiers()
    {
        var novice = AddAbility(1, 0, 9999);
        var maximum = AddAbility(2, 6, 90000);
        _owner.Actability.Regrade(1, true);
        _owner.Actability.Regrade(2, true);
        _owner.Actability.Regrade(uint.MaxValue, true);
        await Assert.That(novice.Step).IsEqualTo((byte)0);
        await Assert.That(maximum.Step).IsEqualTo((byte)6);
    }

    [Test]
    public async Task Regrade_UnlimitedRankDoesNotUseExpansionAsItsLimit()
    {
        _ranks[1].ExpertLimitCount = 0;
        var target = AddAbility(1, 0, 10000);
        for (uint id = 2; id <= 20; id++)
            AddAbility(id, 6);
        _owner.Actability.Regrade(1, true);
        await Assert.That(target.Step).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Downgrade_ReleasesTheHigherSlotAndNeverAddsPoints()
    {
        var target = AddAbility(1, 2, 15000);
        _owner.Actability.Regrade(1, false);
        await Assert.That(target.Step).IsEqualTo((byte)1);
        await Assert.That(target.Point).IsEqualTo(15000);
        target.Point = 20000;
        _owner.Actability.Regrade(1, false);
        await Assert.That(target.Point).IsEqualTo(10000);
        _owner.Actability.Regrade(1, false);
        await Assert.That(target.Step).IsEqualTo((byte)0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Expansion_CommitsSealVocationAndRankTogetherOrRestoresAll(bool committed)
    {
        _expansions[0].LifePoint = 25;
        var seal = AddSeals(2);
        var sawPreparedState = false;
        var result = _owner.Actability.TryExpandExpert(() =>
        {
            sawPreparedState = seal.Count == 1 && _owner.VocationPoint == 75 && _owner.ExpandedExpert == 1;
            return committed;
        });
        await Assert.That(sawPreparedState).IsTrue();
        await Assert.That(result).IsEqualTo(committed);
        await Assert.That(seal.Count).IsEqualTo(committed ? 1 : 2);
        await Assert.That(_owner.VocationPoint).IsEqualTo(committed ? 75 : 100);
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)(committed ? 1 : 0));
    }

    [Test]
    public async Task Expansion_PartialSealPaymentIsRestoredAndDoesNotReachTheCheckpoint()
    {
        _owner.ExpandedExpert = 1;
        var seal = AddSeals(1);
        var reachedCheckpoint = false;
        await Assert.That(_owner.Actability.TryExpandExpert(() => reachedCheckpoint = true)).IsFalse();
        await Assert.That(reachedCheckpoint).IsFalse();
        await Assert.That(seal.Count).IsEqualTo(1);
        await Assert.That(_owner.Inventory.Bag.Items.Contains(seal)).IsTrue();
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Expansion_ChargesTheCurrentRowAndStopsAfterTheLastRow()
    {
        _owner.ExpandedExpert = 13;
        var seal = AddSeals(20);
        await Assert.That(_owner.Actability.TryExpandExpert(() => true)).IsTrue();
        await Assert.That(seal.Count).IsEqualTo(6);
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)14);
        await Assert.That(_owner.Actability.TryExpandExpert(() => throw new InvalidOperationException())).IsFalse();
        await Assert.That(seal.Count).IsEqualTo(6);
        await Assert.That(_owner.VocationPoint).IsEqualTo(100);
    }

    [Test]
    public async Task Expansion_InsufficientVocationDoesNotConsumeTheSeal()
    {
        _expansions[0].LifePoint = 101;
        var seal = AddSeals(1);
        await Assert.That(_owner.Actability.TryExpandExpert(() => throw new InvalidOperationException())).IsFalse();
        await Assert.That(seal.Count).IsEqualTo(1);
        await Assert.That(_owner.VocationPoint).IsEqualTo(100);
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Expansion_UnknownCommitOutcomePreservesPreparedState()
    {
        _expansions[0].LifePoint = 25;
        var seal = AddSeals(2);
        await Assert.That(() => _owner.Actability.TryExpandExpert(() => throw new IOException("Unknown commit outcome"))).Throws<IOException>();
        await Assert.That(seal.Count).IsEqualTo(1);
        await Assert.That(_owner.VocationPoint).IsEqualTo(75);
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Expansion_ConsumesExactPaymentAcrossStacksAndCannotReuseTheSeals()
    {
        _owner.ExpandedExpert = 1;
        var first = AddSeals(1);
        var second = AddSeals(1);
        await Assert.That(_owner.Actability.TryExpandExpert(() => true)).IsTrue();
        await Assert.That(first.Count).IsEqualTo(0);
        await Assert.That(second.Count).IsEqualTo(0);
        await Assert.That(_owner.Inventory.Bag.Items).IsEmpty();
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)2);
        await Assert.That(_owner.Actability.TryExpandExpert(() => throw new InvalidOperationException())).IsFalse();
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)2);
    }

    [Test]
    public async Task Expansion_DoesNotConsumeSealsReservedForTrade()
    {
        var seal = AddSeals(1);
        using var trade = new TradeReservation();
        await Assert.That(trade.TryReserve(seal, 1)).IsTrue();
        await Assert.That(_owner.Actability.TryExpandExpert(() => throw new InvalidOperationException())).IsFalse();
        await Assert.That(seal.Count).IsEqualTo(1);
        await Assert.That(_owner.ExpandedExpert).IsEqualTo((byte)0);
    }

    [Test]
    public async Task AddPoint_StopsAtTheUnlockedRankUntilAnUpgradeSucceeds()
    {
        var target = AddAbility(1, 0, 9995);
        await Assert.That(_owner.Actability.AddPoint(1, 20)).IsEqualTo(5);
        await Assert.That(target.Point).IsEqualTo(10000);
        await Assert.That(_owner.Actability.AddPoint(1, 20)).IsEqualTo(0);
        _owner.Actability.Regrade(1, true);
        await Assert.That(_owner.Actability.AddPoint(1, 20)).IsEqualTo(20);
        await Assert.That(target.Point).IsEqualTo(10020);
    }

    private Actability AddAbility(uint id, byte step, int point = 0)
    {
        var ability = new Actability(new ActabilityTemplate { Id = id }) { Step = step, Point = point };
        _owner.Actability.Actabilities.Add(id, ability);
        return ability;
    }

    private Item AddSeals(int count)
    {
        var item = new Item
        {
            Id = (ulong)_items.Count + 1, TemplateId = _seal.Id, Template = _seal, Count = count,
            OwnerId = _owner.Id, SlotType = SlotType.Inventory, Slot = _items.Count, _holdingContainer = _owner.Inventory.Bag
        };
        _items.Add(item.Id, item);
        _owner.Inventory.Bag.Items.Add(item);
        _owner.Inventory.Bag.UpdateFreeSlotCount();
        return item;
    }

    private void ReplaceSingleton<T>(T value) where T : Singleton<T>
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
