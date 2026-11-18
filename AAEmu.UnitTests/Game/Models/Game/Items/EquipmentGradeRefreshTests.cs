using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class EquipmentGradeRefreshTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private readonly HashSet<uint> _active = [];
    private readonly Dictionary<byte, uint> _grades = new() { [2] = 201, [3] = 202 };
    private CharacterMock _owner;
    private Mock<IBuffs> _buffs;
    private Dictionary<uint, List<BonusTemplate>> _modifiers;

    [Before(Test)]
    public void SetUp()
    {
        var skills = new SkillManager(null, null);
        SetField(skills, "_buffs", new Dictionary<uint, BuffTemplate>
        {
            [201] = new() { Id = 201 }, [202] = new() { Id = 202 }, [209] = new() { Id = 209 },
            [301] = new() { Id = 301 }, [302] = new() { Id = 302 }
        });
        SetField(skills, "_buffTags", new Dictionary<uint, List<uint>>());
        SetInstance(skills);
        var items = new ItemManager(skills, null, null, null, null, null);
        SetField(items, "_config", new ItemConfig { DurabilityConst = 1 });
        SetField(items, "_grades", new Dictionary<int, GradeTemplate>
        {
            [0] = new() { Grade = 0, Durability = 1 },
            [4] = new() { Grade = 4, Durability = 1 },
            [5] = new() { Grade = 5, Durability = 1 }
        });
        _modifiers = [];
        SetField(items, "_itemUnitModifiers", _modifiers);
        SetField(items, "_itemProcBindings", new Dictionary<uint, List<uint>>());
        SetField(items, "_equipItemSets", new Dictionary<uint, EquipItemSet>());
        SetField(items, "_armorGradeBuffs", new Dictionary<ArmorType, Dictionary<ItemGrade, ArmorGradeBuff>>
        {
            [ArmorType.Cloth] = new()
            {
                [ItemGrade.Arcane] = new() { BuffId = 301 },
                [ItemGrade.Heroic] = new() { BuffId = 302 }
            }
        });
        SetInstance(items);
        var itemData = new ItemGameData();
        SetField(itemData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>> { [100] = _grades });
        SetInstance(itemData);
        _buffs = Mock.Of<IBuffs>();
        foreach (var id in new uint[] { 201, 202, 209, 301, 302 })
        {
            _buffs.AddBuff(Is<Buff>(buff => buff.Template.Id == id), 0, 0).Callback(() => _active.Add(id));
            _buffs.RemoveBuff(id).Callback(() => _active.Remove(id));
        }
        _buffs.RemoveBuffs((uint)BuffConstants.ArmorBuffTag, 10).Callback(() => _active.ExceptWith([301, 302]));
        _owner = new CharacterMock { Id = 1, ObjId = 1, Buffs = _buffs.Object };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
        _previous.Clear();
        _active.Clear();
    }

    [Test]
    [Arguments((byte)2, (byte)3, 201u, 202u)]
    [Arguments((byte)3, (byte)2, 202u, 201u)]
    public async Task Regrade_EquippedPiece_ReplacesOldGradeBuffAndRefreshesBonuses(byte oldGrade, byte newGrade,
        uint oldBuff, uint newBuff)
    {
        var item = Equip(new EquipItemTemplate { Id = 100, BuffId = 209 }, oldGrade);
        _active.Add(oldBuff);
        var oldBonus = new Bonus { Template = new BonusTemplate { Value = 999 }, Value = 999 };
        _owner.Bonuses[Buffs.GearBonusesIndex] = [oldBonus];
        var bonus = new BonusTemplate { Value = 5 };
        _modifiers[100] = [bonus];
        item.Grade = newGrade;

        GradeEnchant.RefreshWornPiece(_owner, item, oldGrade);

        await Assert.That(_active.Contains(oldBuff)).IsFalse();
        await Assert.That(_active.Contains(newBuff)).IsTrue();
        await Assert.That(_owner.Bonuses[Buffs.GearBonusesIndex].Single().Template).IsSameReferenceAs(bonus);
        _buffs.AddBuff(Is<Buff>(buff => buff.Template.Id == 209), 0, 0).WasCalled(Times.Never);
    }

    [Test]
    public async Task Regrade_PreviousGradeHasNoRow_RemovesTheItemFallbackBuff()
    {
        _grades.Remove(2);
        var item = Equip(new EquipItemTemplate { Id = 100, BuffId = 209 }, 3);
        _active.Add(209);

        GradeEnchant.RefreshWornPiece(_owner, item, 2);

        await Assert.That(_active.Contains(209)).IsFalse();
        await Assert.That(_active.Contains(202)).IsTrue();
    }

    [Test]
    public async Task Regrade_NewGradeHasNoRow_AppliesTheItemFallbackBuff()
    {
        _grades.Remove(3);
        var item = Equip(new EquipItemTemplate { Id = 100, BuffId = 209 }, 3);
        _active.Add(201);

        GradeEnchant.RefreshWornPiece(_owner, item, 2);

        await Assert.That(_active.Contains(201)).IsFalse();
        await Assert.That(_active.Contains(209)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Regrade_UnchangedGradeOrBagItem_DoesNotRefreshEquipment(bool bagItem)
    {
        var item = Equip(new EquipItemTemplate { Id = 100 }, bagItem ? (byte)3 : (byte)2);
        if (bagItem)
            item.SlotType = SlotType.Inventory;
        _active.Add(201);

        GradeEnchant.RefreshWornPiece(_owner, item, 2);

        await Assert.That(_active.SetEquals([201])).IsTrue();
        _buffs.RemoveBuff(201).WasCalled(Times.Never);
    }

    [Test]
    public async Task Regrade_OnePiece_DoesNotReapplyAnUnrelatedEquippedBuff()
    {
        var changed = Equip(new EquipItemTemplate { Id = 100 }, 3);
        Equip(new EquipItemTemplate { Id = 101, BuffId = 209 }, 2);
        _active.UnionWith([201, 209]);

        GradeEnchant.RefreshWornPiece(_owner, changed, 2);

        await Assert.That(_active.SetEquals([202, 209])).IsTrue();
        _buffs.AddBuff(Is<Buff>(buff => buff.Template.Id == 209), 0, 0).WasCalled(Times.Never);
        _buffs.RemoveBuff(209).WasCalled(Times.Never);
    }

    [Test]
    public async Task Regrade_FourWornArmorPieces_RecalculatesTheLowestGradeBuff()
    {
        var template = new ArmorTemplate
        {
            Id = 110, Level = 20,
            KindTemplate = new WearableKind { TypeId = (uint)ArmorType.Cloth },
            WearableTemplate = new Wearable { TypeId = (uint)ArmorType.Cloth },
            SlotTemplate = new WearableSlot { SlotTypeId = (uint)EquipmentItemSlotType.Head }
        };
        var changed = Equip(template, 4, armor: true);
        for (var i = 0; i < 3; i++)
            Equip(template, 5, armor: true);
        _owner.UpdateGearBonuses(changed, null);
        await Assert.That(_active.Contains(301)).IsTrue();
        changed.Grade = 5;

        GradeEnchant.RefreshWornPiece(_owner, changed, 4);

        await Assert.That(_active.Contains(301)).IsFalse();
        await Assert.That(_active.Contains(302)).IsTrue();
    }

    private EquipItem Equip(EquipItemTemplate template, byte grade, bool armor = false)
    {
        EquipItem item = armor ? new Armor(0, template, 1) : new EquipItem(0, template, 1);
        item.Id = (ulong)_owner.Equipment.Items.Count + 1;
        item.Grade = grade;
        item.Slot = _owner.Equipment.Items.Count;
        item.SlotType = SlotType.Equipment;
        item._holdingContainer = _owner.Equipment;
        _owner.Equipment.Items.Add(item);
        return item;
    }

    private void SetInstance<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static void SetField(object owner, string name, object value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
}
