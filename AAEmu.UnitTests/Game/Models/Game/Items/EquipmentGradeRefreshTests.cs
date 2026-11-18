using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class EquipmentGradeRefreshTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private HashSet<uint> ActiveIds => _owner.Buffs.GetEffectsByType(typeof(BuffTemplate))
        .Select(buff => buff.Template.Id).ToHashSet();
    private readonly Dictionary<byte, uint> _grades = new() { [2] = 201, [3] = 202 };
    private CharacterMock _owner;
    private Dictionary<uint, BuffTemplate> _templates;
    private Dictionary<uint, List<BonusTemplate>> _modifiers;

    [Before(Test)]
    public void SetUp()
    {
        var skills = new SkillManager(null, null);
        _templates = new uint[] { 201, 202, 209, 301, 302 }.ToDictionary(id => id, id => new BuffTemplate
        {
            Id = id, Kind = BuffKind.Good, StackRule = BuffStackRule.Independent, MaxStack = 2
        });
        SetField(skills, "_buffs", _templates);
        SetField(skills, "_buffTags", new Dictionary<uint, List<uint>>());
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>
        {
            [(uint)BuffConstants.ArmorBuffTag] = [301, 302]
        });
        SetField(skills, "_buffTriggers", new Dictionary<uint, List<BuffTriggerTemplate>>());
        SetField(skills, "_skillModifiers", new Dictionary<uint, List<SkillModifier>>());
        SetField(skills, "_combatBuffs", new Dictionary<uint, List<CombatBuffTemplate>>());
        SetInstance(skills);
        var buffData = new BuffGameData();
        SetField(buffData, "_buffModifiers", new Dictionary<uint, List<BuffModifier>>());
        SetInstance(buffData);
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
        _owner = new CharacterMock { Id = 1, ObjId = 1 };

    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
        _previous.Clear();
    }

    [Test]
    [Arguments((byte)2, (byte)3, 201u, 202u)]
    [Arguments((byte)3, (byte)2, 202u, 201u)]
    public async Task Regrade_EquippedPiece_ReplacesOldGradeBuffAndRefreshesBonuses(byte oldGrade, byte newGrade,
        uint oldBuff, uint newBuff)
    {
        var item = Equip(new EquipItemTemplate { Id = 100, BuffId = 209 }, oldGrade);
        AddBuff(oldBuff);
        var oldBonus = new Bonus { Template = new BonusTemplate { Value = 999 }, Value = 999 };
        _owner.Bonuses[Buffs.GearBonusesIndex] = [oldBonus];
        var bonus = new BonusTemplate { Value = 5 };
        _modifiers[100] = [bonus];
        item.Grade = newGrade;

        GradeEnchant.RefreshWornPiece(_owner, item, oldGrade);

        await Assert.That(ActiveIds.Contains(oldBuff)).IsFalse();
        await Assert.That(ActiveIds.Contains(newBuff)).IsTrue();
        await Assert.That(_owner.Bonuses[Buffs.GearBonusesIndex].Single().Template).IsSameReferenceAs(bonus);
        await Assert.That(ActiveIds.Contains(209)).IsFalse();
    }

    [Test]
    public async Task Regrade_PreviousGradeHasNoRow_RemovesTheItemFallbackBuff()
    {
        _grades.Remove(2);
        var item = Equip(new EquipItemTemplate { Id = 100, BuffId = 209 }, 3);
        AddBuff(209);

        GradeEnchant.RefreshWornPiece(_owner, item, 2);

        await Assert.That(ActiveIds.Contains(209)).IsFalse();
        await Assert.That(ActiveIds.Contains(202)).IsTrue();
    }

    [Test]
    public async Task Regrade_NewGradeHasNoRow_AppliesTheItemFallbackBuff()
    {
        _grades.Remove(3);
        var item = Equip(new EquipItemTemplate { Id = 100, BuffId = 209 }, 3);
        AddBuff(201);

        GradeEnchant.RefreshWornPiece(_owner, item, 2);

        await Assert.That(ActiveIds.Contains(201)).IsFalse();
        await Assert.That(ActiveIds.Contains(209)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Regrade_UnchangedGradeOrBagItem_DoesNotRefreshEquipment(bool bagItem)
    {
        var item = Equip(new EquipItemTemplate { Id = 100 }, bagItem ? (byte)3 : (byte)2);
        if (bagItem)
            item.SlotType = SlotType.Inventory;
        AddBuff(201);

        GradeEnchant.RefreshWornPiece(_owner, item, 2);

        await Assert.That(ActiveIds.SetEquals([201])).IsTrue();
        await Assert.That(_owner.Buffs.GetBuffCountById(201)).IsEqualTo(1);
    }

    [Test]
    public async Task Regrade_OnePiece_DoesNotReapplyAnUnrelatedEquippedBuff()
    {
        var changed = Equip(new EquipItemTemplate { Id = 100 }, 3);
        Equip(new EquipItemTemplate { Id = 101, BuffId = 209 }, 2);
        AddBuff(201);
        var unrelated = AddBuff(209);

        GradeEnchant.RefreshWornPiece(_owner, changed, 2);

        await Assert.That(ActiveIds.SetEquals([202, 209])).IsTrue();
        await Assert.That(_owner.Buffs.GetEffectFromBuffId(209)).IsSameReferenceAs(unrelated);
    }

    [Test]
    [Arguments(20)]
    [Arguments(40)]
    public async Task Regrade_SharedIndependentBuff_RetainsTheOtherPiecesLevel(int changedLevel)
    {
        _templates[201].Bonuses.Add(new BonusTemplate
        {
            Attribute = UnitAttribute.Str, ModifierType = UnitModifierType.Value, LinearLevelBonus = 100
        });
        _templates[202].Bonuses.Add(new BonusTemplate
        {
            Attribute = UnitAttribute.Str, ModifierType = UnitModifierType.Value, LinearLevelBonus = 200
        });
        var unchangedLevel = changedLevel == 20 ? 40 : 20;
        Equip(new EquipItemTemplate { Id = 100, Level = unchangedLevel }, 2);
        var unchanged = AddBuff(201, (uint)unchangedLevel);
        var changed = Equip(new EquipItemTemplate { Id = 100, Level = changedLevel }, 2);
        AddBuff(201, (uint)changedLevel);
        changed.Grade = 3;

        GradeEnchant.RefreshWornPiece(_owner, changed, 2);

        await Assert.That(_owner.Buffs.GetBuffCountById(201)).IsEqualTo(1);
        await Assert.That(_owner.Buffs.GetEffectFromBuffId(201)).IsSameReferenceAs(unchanged);
        await Assert.That(_owner.Bonuses[unchanged.Index].Single().Value).IsEqualTo(unchangedLevel);
        var current = _owner.Buffs.GetEffectFromBuffId(202);
        await Assert.That(current.AbLevel).IsEqualTo((uint)changedLevel);
        await Assert.That(_owner.Bonuses[current.Index].Single().Value).IsEqualTo(changedLevel * 2);
    }

    [Test]
    public async Task Regrade_TwoEqualLevelPiecesWithSharedBuff_KeepsOneOldAndOneNewEffect()
    {
        Equip(new EquipItemTemplate { Id = 100, Level = 20 }, 2);
        AddBuff(201, 20);
        var changed = Equip(new EquipItemTemplate { Id = 100, Level = 20 }, 3);
        AddBuff(201, 20);

        GradeEnchant.RefreshWornPiece(_owner, changed, 2);

        await Assert.That(_owner.Buffs.GetBuffCountById(201)).IsEqualTo(1);
        await Assert.That(_owner.Buffs.GetBuffCountById(202)).IsEqualTo(1);
    }

    [Test]
    public async Task Regrade_BothGradesUseTheSameBuff_DoesNotRestartOrDuplicateIt()
    {
        _grades[3] = 201;
        var changed = Equip(new EquipItemTemplate { Id = 100, Level = 20 }, 3);
        var original = AddBuff(201, 20);

        GradeEnchant.RefreshWornPiece(_owner, changed, 2);

        await Assert.That(_owner.Buffs.GetBuffCountById(201)).IsEqualTo(1);
        await Assert.That(_owner.Buffs.GetEffectFromBuffId(201)).IsSameReferenceAs(original);
    }

    [Test]
    public async Task Regrade_ChargedPiece_PreservesItsActiveChargeEffectAndTimes()
    {
        var changed = Equip(new EquipItemTemplate
        {
            Id = 100, RechargeBuffId = 209, ChargeLifetime = 60, ChargeCount = 5
        }, 3);
        changed.ChargeStartTime = DateTime.UtcNow.AddMinutes(-1);
        changed.ChargeCount = 3;
        AddBuff(201);
        var charged = AddBuff(209);
        charged.StartTime = DateTime.UtcNow.AddSeconds(-20);
        charged.EndTime = DateTime.UtcNow.AddSeconds(40);
        charged.Charge = 3;
        var start = charged.StartTime;
        var end = charged.EndTime;

        GradeEnchant.RefreshWornPiece(_owner, changed, 2);

        await Assert.That(_owner.Buffs.GetBuffCountById(209)).IsEqualTo(1);
        await Assert.That(_owner.Buffs.GetEffectFromBuffId(209)).IsSameReferenceAs(charged);
        await Assert.That(charged.StartTime).IsEqualTo(start);
        await Assert.That(charged.EndTime).IsEqualTo(end);
        await Assert.That(charged.Charge).IsEqualTo(3);
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
        await Assert.That(ActiveIds.Contains(301)).IsTrue();
        changed.Grade = 5;

        GradeEnchant.RefreshWornPiece(_owner, changed, 4);

        await Assert.That(ActiveIds.Contains(301)).IsFalse();
        await Assert.That(ActiveIds.Contains(302)).IsTrue();
    }

    private Buff AddBuff(uint id, uint level = 0)
    {
        var buff = new Buff(_owner, _owner, new SkillCasterUnit(), _templates[id], null, DateTime.UtcNow)
        {
            AbLevel = level
        };
        _owner.Buffs.AddBuff(buff);
        return buff;
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
