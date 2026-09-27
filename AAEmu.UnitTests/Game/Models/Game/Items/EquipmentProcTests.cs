using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Procs;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class EquipmentProcTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private readonly Dictionary<uint, List<uint>> _bindings = [];
    private readonly Dictionary<uint, ItemProcTemplate> _templates = [];
    private readonly Dictionary<uint, EquipItemSet> _sets = [];
    private readonly List<(uint SkillId, int ItemLevel)> _casts = [];
    private ItemManager _items;
    private EquipmentUnit _owner;
    private Mock<IBuffs> _buffs;
    private DateTime _now;
    private double _roll;

    [Before(Test)]
    public void SetUp()
    {
        _now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        _roll = 0;
        var skills = new SkillManager(null, null);
        SetField(skills, "_buffs", new Dictionary<uint, BuffTemplate>());
        SetField(skills, "_buffTags", new Dictionary<uint, List<uint>>());
        SetInstance(skills);
        _items = new ItemManager(skills, null, null, null, null, null);
        SetField(_items, "_itemProcBindings", _bindings);
        SetField(_items, "_itemProcTemplates", _templates);
        SetField(_items, "_equipItemSets", _sets);
        SetField(_items, "_itemUnitModifiers", new Dictionary<uint, List<BonusTemplate>>());
        SetInstance(_items);
        var itemData = new ItemGameData();
        SetField(itemData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
        SetInstance(itemData);
        _buffs = Mock.Of<IBuffs>();
        _owner = new EquipmentUnit { ObjId = 1, Hp = 100, Buffs = _buffs.Object };
        _owner.SetProcs(new UnitProcs(_owner, () => _items, () => _now, () => _roll,
            (_, _, skill, itemLevel) =>
            {
                _casts.Add((skill.Id, itemLevel));
                return true;
            }));
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
        _previous.Clear();
        _bindings.Clear();
        _templates.Clear();
        _sets.Clear();
        _casts.Clear();
    }

    [Test]
    public async Task Collect_DirectHoldableRuneAndCompletedSet_RetainsEveryAuthoredSource()
    {
        // r208022 holdable proc 27, lunafrost 26855/proc 72, and set 125/proc 46.
        _bindings[100] = [41];
        _bindings[26855] = [72];
        var first = Equip(1, new WeaponTemplate
        {
            Id = 100, Level = 40, EquipItemSetId = 125,
            HoldableTemplate = new Holdable { ItemProcId = 27 }
        });
        first.RuneId = 26855;
        var second = Equip(2, Template(101, 20, 125));
        SetBonus(125, 2, 46);

        var sources = EquipmentProcSource.Collect([first, second], _items);

        await Assert.That(sources.Count).IsEqualTo(4);
        await Assert.That(sources.Single(source => source.Key == new EquipmentProcKey(1, 0, 41)).ItemLevel).IsEqualTo(40);
        await Assert.That(sources.Single(source => source.Key == new EquipmentProcKey(1, 0, 27)).ItemLevel).IsEqualTo(40);
        await Assert.That(sources.Single(source => source.Key == new EquipmentProcKey(1, 0, 72)).ItemLevel).IsEqualTo(40);
        await Assert.That(sources.Single(source => source.Key == new EquipmentProcKey(0, 125, 46)).ItemLevel).IsEqualTo(20);
    }

    [Test]
    public async Task Collect_OneItemHasTheSameProcThroughSeveralSources_DoesNotMultiplyItsRolls()
    {
        AddProc(27);
        _bindings[100] = [27, 27];
        _bindings[26855] = [27];
        var item = Equip(1, new WeaponTemplate
        {
            Id = 100, Level = 40, HoldableTemplate = new Holdable { ItemProcId = 27 }
        });
        item.RuneId = 26855;

        _owner.Procs.RefreshEquipment();
        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(_casts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RefreshEquipment_RuneReplacement_RemovesTheOldProcAndAttachesTheNewProc()
    {
        AddProc(72);
        AddProc(73);
        _bindings[26855] = [72];
        _bindings[26856] = [73];
        var item = Equip(1, Template(100, 45));
        item.RuneId = 26855;
        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        item.RuneId = 26856;
        _owner.UpdateGearBonuses(null, null);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(_casts.Select(cast => cast.SkillId).ToArray()).IsEquivalentTo(new uint[] { 72, 73 });
        item.RuneId = 0;
        _owner.UpdateGearBonuses(null, null);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Collect_BrokenEquipment_RemovesItsDirectHoldableRuneAndSetContributions()
    {
        _bindings[100] = [41];
        _bindings[26855] = [72];
        var broken = Equip(1, new WeaponTemplate
        {
            Id = 100, Level = 40, EquipItemSetId = 125,
            HoldableTemplate = new Holdable { ItemProcId = 27 }
        });
        broken.RuneId = 26855;
        broken.Durability = 0;
        var intact = Equip(2, Template(101, 40, 125));
        SetBonus(125, 2, 46);

        var sources = EquipmentProcSource.Collect([broken, intact], _items);

        await Assert.That(sources.Count).IsEqualTo(0);
        broken.Durability = 10;
        await Assert.That(EquipmentProcSource.Collect([broken, intact], _items).Count).IsEqualTo(4);
    }

    [Test]
    public async Task RefreshEquipment_TwoSourcesShareAProc_UsesTheHighestLevelUntilThatSourceLeaves()
    {
        var proc = AddProc(27);
        proc.ChanceRate = 0;
        proc.ItemLevelBasedChanceBonus = 100;
        _bindings[100] = [27];
        _bindings[101] = [27];
        var low = Equip(1, Template(100, 20));
        var high = Equip(2, Template(101, 50));
        _roll = 0.4;
        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(1);
        await Assert.That(_casts[0].ItemLevel).IsEqualTo(50);

        high._holdingContainer = null;
        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(1);

        _roll = 0.1;
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(2);
        await Assert.That(_casts[1].ItemLevel).IsEqualTo(20);

        low._holdingContainer = null;
        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RefreshSources_AnotherItemKeepsTheProc_DoesNotResetItsSharedCooldown()
    {
        AddProc(41, 10);
        _owner.Procs.RefreshSources([
            new EquipmentProcSource(new EquipmentProcKey(1, 0, 41), 20),
            new EquipmentProcSource(new EquipmentProcKey(2, 0, 41), 50)
        ]);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        _now = _now.AddSeconds(5);
        _owner.Procs.RefreshSources([new EquipmentProcSource(new EquipmentProcKey(1, 0, 41), 20)]);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(1);

        _now = _now.AddSeconds(5);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(2);
        await Assert.That(_casts[1].ItemLevel).IsEqualTo(20);
    }

    [Test]
    public async Task MoveToBag_OnLeaveRunsBeforeSourceListRemoval_StillDetachesTheProc()
    {
        AddProc(41);
        _bindings[100] = [41];
        var equipment = new ObservedEquipment(_owner);
        _owner.Equipment = equipment;
        var item = Equip(1, Template(100));
        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        var bag = CreateBag();

        var moved = bag.AddOrMoveExistingItem(ItemTaskType.Invalid, item, 0, false, false);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(moved).IsTrue();
        await Assert.That(equipment.SawItemInOldListDuringLeave).IsTrue();
        await Assert.That(equipment.SawNewContainerDuringLeave).IsTrue();
        await Assert.That(item._holdingContainer).IsSameReferenceAs(bag);
        await Assert.That(equipment.Items.Contains(item)).IsFalse();
        await Assert.That(_casts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnequipAndReequip_PreservesTheCooldownUntilItsOriginalExpiry()
    {
        AddProc(41, 10);
        _bindings[100] = [41];
        var item = Equip(1, Template(100));
        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        var bag = CreateBag();
        _now = _now.AddSeconds(1);
        await Assert.That(bag.AddOrMoveExistingItem(ItemTaskType.Invalid, item, 0, false, false)).IsTrue();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        _now = _now.AddSeconds(1);
        await Assert.That(_owner.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, item,
            (int)EquipmentItemSlot.Head, false, false)).IsTrue();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(1);

        _now = _now.AddSeconds(8);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ExistingSetBuff_DoesNotSkipItsProc_AndLastPieceRemovalClearsBoth()
    {
        AddProc(46);
        SetBonus(125, 1, 46, 801);
        _buffs.CheckBuff(801).Returns(true);
        var item = Equip(1, Template(100, 50, 125));

        _owner.UpdateGearBonuses(null, null);
        _owner.UpdateGearBonuses(null, null);
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(1);
        _buffs.AddBuff(Is<Buff>(buff => buff.Template.Id == 801), 0, 0).WasCalled(Times.Never);

        await Assert.That(CreateBag().AddOrMoveExistingItem(ItemTaskType.Invalid, item, 0, false, false)).IsTrue();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);
        await Assert.That(_casts.Count).IsEqualTo(1);
        _buffs.RemoveBuff(801).WasCalled(Times.Once);
    }

    [Test]
    public async Task RefreshEquipment_MissingProcReference_DoesNotPreventValidBindings()
    {
        AddProc(41);
        _bindings[100] = [999, 41];
        Equip(1, Template(100));

        _owner.Procs.RefreshEquipment();
        _owner.Procs.RollProcsForKind(ProcChanceKind.HitAny);

        await Assert.That(_casts.Count).IsEqualTo(1);
        await Assert.That(_casts[0].SkillId).IsEqualTo(41u);
    }

    [Test]
    public async Task ReadItemProcBindings_UnorderedDuplicateRows_PreservesEveryDistinctBinding()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE item_proc_bindings(id INTEGER, item_id INTEGER, proc_id INTEGER);
            INSERT INTO item_proc_bindings VALUES
                (4, 100, 27), (2, 26855, 72), (3, 100, 41), (1, 100, 27);
            """;
        command.ExecuteNonQuery();

        var bindings = ItemManager.ReadItemProcBindings(connection);

        await Assert.That(bindings.Count).IsEqualTo(2);
        await Assert.That(bindings[100].Count).IsEqualTo(2);
        await Assert.That(bindings[100][0]).IsEqualTo(27u);
        await Assert.That(bindings[100][1]).IsEqualTo(41u);
        await Assert.That(bindings[26855].Single()).IsEqualTo(72u);
        command.CommandText = "DELETE FROM item_proc_bindings;";
        command.ExecuteNonQuery();
        await Assert.That(ItemManager.ReadItemProcBindings(connection).Count).IsEqualTo(0);
    }

    private ItemProcTemplate AddProc(uint id, uint cooldown = 0)
    {
        var template = new ItemProcTemplate
        {
            Id = id, ChanceKind = ProcChanceKind.HitAny, ChanceRate = 100, CooldownSec = cooldown,
            SkillId = id, SkillTemplate = new SkillTemplate { Id = id, TargetType = SkillTargetType.Self }
        };
        _templates.Add(id, template);
        return template;
    }

    private void SetBonus(uint setId, int pieces, uint procId, uint buffId = 0)
    {
        var set = new EquipItemSet { Id = setId };
        set.Bonuses.Add(new EquipItemSetBonus { NumPieces = pieces, ItemProcId = procId, BuffId = buffId });
        _sets.Add(setId, set);
    }

    private static EquipItemTemplate Template(uint id, int level = 40, uint setId = 0)
    {
        return new EquipItemTemplate { Id = id, Level = level, MaxCount = 1, EquipItemSetId = setId };
    }

    private DurableEquipment Equip(ulong id, EquipItemTemplate template)
    {
        var item = new DurableEquipment(id, template)
        {
            Durability = 10, SlotType = SlotType.Equipment, Slot = (int)id - 1,
            _holdingContainer = _owner.Equipment
        };
        _owner.Equipment.Items.Add(item);
        return item;
    }

    private ItemContainer CreateBag()
    {
        return new ItemContainer(0, SlotType.Inventory, false, _owner) { ContainerSize = 10 };
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField<T>(T instance, string field, object value)
    {
        typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    }

    private sealed class EquipmentUnit : Unit
    {
        public void SetProcs(UnitProcs procs) => Procs = procs;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class DurableEquipment(ulong id, EquipItemTemplate template) : EquipItem(id, template, 1)
    {
        public override byte MaxDurability => 10;
    }

    private sealed class ObservedEquipment(Unit owner) : EquipmentContainer(0, SlotType.Equipment, false, owner)
    {
        public bool SawItemInOldListDuringLeave { get; private set; }
        public bool SawNewContainerDuringLeave { get; private set; }

        public override void OnLeaveContainer(Item item, ItemContainer newContainer, byte previousSlot)
        {
            SawItemInOldListDuringLeave = Items.Contains(item);
            SawNewContainerDuringLeave = ReferenceEquals(item._holdingContainer, newContainer);
            base.OnLeaveContainer(item, newContainer, previousSlot);
        }
    }
}
