using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class ItemEligibilityTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private Dictionary<ulong, Item> _items;
    private Dictionary<uint, ItemTemplate> _templates;
    private CharacterMock _owner;
    private ItemContainer _bag;

    [Before(Test)]
    public void SetUp()
    {
        var skills = new SkillManager(null, null);
        SetField(skills, "_skillReagents", new Dictionary<uint, SkillReagent>());
        SetField(skills, "_skillProducts", new Dictionary<uint, SkillProduct>());
        SetInstance(skills);
        SetInstance(new UnitRequirementsGameData());
        var manager = new ItemManager(skills, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        SetInstance(manager);
        SetInstance(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        SetInstance(new AccountManager(null, null, TimeProvider.System));
        var formulas = new FormulaManager();
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>());
        SetInstance(formulas);
        _items = [];
        _templates = [];
        SetField(manager, "_allItems", _items);
        SetField(manager, "_removedItems", new List<ulong>());
        SetField(manager, "_templates", _templates);
        SetField(manager, "_defaultDyeIds", new Dictionary<uint, uint> { [200] = 50 });
        _owner = new CharacterMock { Id = 7, ObjId = 70, Hp = 100, Mp = 100, ConditionChance = true, Level = 20, Gender = Gender.Male, Money = 100, NumInventorySlots = 10 };
        _owner.InitializeLaborCache(20, DateTime.UtcNow);
        var containers = new Dictionary<ulong, ItemContainer>();
        ulong id = 1;
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(_owner.Id, type, false, _owner) { ContainerId = id++, Owner = _owner };
            containers.Add(container.ContainerId, container);
        }
        SetField(manager, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
        _bag = _owner.Inventory.Bag;
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
        _previous.Clear();
    }

    [Test]
    [Arguments(21, 0, 0, ErrorMessageType.LevelLowToEquip)]
    [Arguments(20, 0, 0, ErrorMessageType.NoErrorMessage)]
    [Arguments(0, 19, 0, ErrorMessageType.LevelHighToEquip)]
    [Arguments(0, 20, 0, ErrorMessageType.NoErrorMessage)]
    [Arguments(0, 0, 2, ErrorMessageType.NoMatchGenderToEquip)]
    [Arguments(0, 0, 1, ErrorMessageType.NoErrorMessage)]
    public async Task Equip_Requirements_KeepRejectedItemInBag(int minimum, int maximum, int gender, ErrorMessageType error)
    {
        var template = new ArmorTemplate
        {
            Id = 200, MaxCount = 1, LevelRequirement = minimum, LevelLimit = maximum, CharGender = (byte)gender,
            BindType = ItemBindType.BindOnEquip, WearableTemplate = new Wearable { SlotTypeId = 1 }
        };
        var item = AddEquipment(1, template);
        var equipment = new EquipmentContainer(_owner.Id, SlotType.Equipment, false, _owner) { Owner = _owner };
        bool accepted;
        lock (SaveManager.PersistenceSyncRoot)
        {
            using var mutation = new InventoryMutation(ItemTaskType.Invalid);
            accepted = mutation.TryMove(item, equipment, (int)EquipmentItemSlot.Head);
        }
        await Assert.That(EquipmentContainer.GetEquipRequirementError(template, _owner)).IsEqualTo(error);
        await Assert.That(accepted).IsEqualTo(error == ErrorMessageType.NoErrorMessage);
        await Assert.That(item._holdingContainer).IsSameReferenceAs(_bag);
        await Assert.That(item.HasFlag(ItemFlag.SoulBound)).IsFalse();
    }

    [Test]
    public async Task SavedEquipment_RestoresOldGearWithoutNewEquipPermission()
    {
        var template = new ArmorTemplate
        {
            Id = 200, LevelRequirement = 50, CharGender = 2, BindType = ItemBindType.BindOnEquip,
            WearableTemplate = new Wearable { SlotTypeId = 1 }
        };
        var item = new EquipItem(1, template, 1) { OwnerId = _owner.Id, SlotType = SlotType.Equipment, Slot = 0 };
        var equipment = new EquipmentContainer(_owner.Id, SlotType.Equipment, false, null) { ContainerId = 9 };
        await Assert.That(equipment.RestorePersistedItem(item)).IsTrue();
        await Assert.That(item._holdingContainer).IsSameReferenceAs(equipment);
        await Assert.That(item.HasFlag(ItemFlag.SoulBound)).IsFalse();
        equipment.Owner = _owner;
        equipment.ParentUnit = _owner;
        await Assert.That(equipment.CanAccept(item, 0)).IsFalse();
        await Assert.That(equipment.RestorePersistedItem(new EquipItem(2, template, 1))).IsFalse();
    }

    [Test]
    [Arguments(false, true, 1)]
    [Arguments(true, false, 1)]
    [Arguments(true, true, 2)]
    [Arguments(true, true, 24)]
    public async Task Regrade_InvalidEligibility_DoesNotDebitAssets(bool gradable, bool enchantable, int scrollType)
    {
        var equipment = AddEquipment(1, new WeaponTemplate
        {
            Id = 200, MaxCount = 1, Gradable = gradable, GradeEnchantable = enchantable,
            HoldableTemplate = new Holdable { SlotTypeId = 1 }
        });
        equipment.Grade = 3;
        var scroll = AddItem(2, new ItemTemplate { Id = 100, MaxCount = 10 }, 2);
        SetField(ItemManager.Instance, "_grades", new Dictionary<int, GradeTemplate> { [3] = new() { Grade = 3 } });
        var skill = new Skill(new SkillTemplate { Id = 22520, ConsumeLaborPower = 10 });
        var checkpoint = false;
        skill.CommitLaborBatch = (_, _) => checkpoint = true;
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            new GradeEnchant().Execute(_owner, new SkillItem { ItemId = scroll.Id, ItemTemplateId = 100 },
                _owner, new SkillCastItemTarget { Id = equipment.Id }, null, skill, null,
                DateTime.UtcNow, 0, 0, scrollType, 0);
            if (!skill.Cancelled)
                SkillLaborBatch.Current.Inventory.TryConsume(_bag, scroll, 1);
        });
        await Assert.That(result).IsFalse();
        await Assert.That(checkpoint).IsFalse();
        await Assert.That(equipment.Grade).IsEqualTo((byte)3);
        await Assert.That(scroll.Count).IsEqualTo(2);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
    }

    [Test]
    [Arguments("category")]
    [Arguments("missing-rule")]
    [Arguments("not-equipment")]
    [Arguments("same-item")]
    [Arguments("missing-powder")]
    [Arguments("reserved-image")]
    public async Task Appearance_InvalidPairOrPayment_LeavesAllItemsUnchanged(string rejection)
    {
        var item = AddEquipment(1, AppearanceTemplate(200, 1));
        var image = AddEquipment(2, AppearanceTemplate(201, 1));
        var powder = AddItem(3, new ItemTemplate { Id = 100, MaxCount = 100 }, 2);
        using var reservation = new TradeReservation();
        switch (rejection)
        {
            case "category": ((EquipItemTemplate)image.Template).ItemLookConvert.Id = 2; break;
            case "missing-rule": ((EquipItemTemplate)item.Template).ItemLookConvert = null; break;
            case "not-equipment": image.Template = new ItemTemplate(); break;
            case "same-item": image = item; break;
            case "missing-powder": ((EquipItemTemplate)item.Template).ItemLookConvert.RequiredItemCount = 3; break;
            case "reserved-image": reservation.TryReserve(image, 1); break;
        }
        powder.IsDirty = false;
        var result = CSConvertItemLookPacket.Convert(_owner, item.Id, image.Id);
        await Assert.That(result).IsFalse();
        await Assert.That(item.ImageItemTemplateId).IsEqualTo(0U);
        await Assert.That(powder.Count).IsEqualTo(2);
        await Assert.That(powder.IsDirty).IsFalse();
        await Assert.That(image.Count).IsEqualTo(1);
        await Assert.That(_bag.Items.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Appearance_ValidPair_ConsumesExactImageAndPowder()
    {
        var item = AddEquipment(1, AppearanceTemplate(200, 1));
        var image = AddEquipment(2, AppearanceTemplate(201, 1));
        var powder1 = AddItem(3, new ItemTemplate { Id = 100, MaxCount = 100 }, 1);
        var powder2 = AddItem(4, powder1.Template, 3);
        await Assert.That(CSConvertItemLookPacket.Convert(_owner, item.Id, image.Id)).IsTrue();
        await Assert.That(item.ImageItemTemplateId).IsEqualTo(201U);
        await Assert.That(image.Count).IsEqualTo(0);
        await Assert.That(powder1.Count).IsEqualTo(0);
        await Assert.That(powder2.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dye_Checkpoint_SettlesAppearanceAndDyeTogether(bool commit)
    {
        var equipment = AddEquipment(1, AppearanceTemplate(200, 1));
        equipment.DyeItemId = 50;
        equipment.IsDirty = false;
        var dye = AddItem(2, new ItemTemplate { Id = 100, MaxCount = 100, CategoryId = 33, UseSkillId = 22727 }, 2);
        var skill = DyeSkill();
        var sawPrepared = false;
        skill.CommitLaborBatch = (_, _) => { sawPrepared = equipment.DyeItemId == 100 && dye.Count == 1; return commit; };
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            Dye(equipment, dye, skill);
            if (!skill.Cancelled)
                SkillLaborBatch.Current.Inventory.TryConsume(_bag, dye, 1);
        });
        await Assert.That(Dyeing.IsDyeingSkill(skill)).IsTrue();
        await Assert.That(sawPrepared).IsTrue();
        await Assert.That(result).IsEqualTo(commit);
        await Assert.That(equipment.DyeItemId).IsEqualTo(commit ? 100U : 50U);
        await Assert.That(equipment.IsDirty).IsEqualTo(commit);
        await Assert.That(dye.Count).IsEqualTo(commit ? 1 : 2);
    }

    [Test]
    [Arguments("non-dyeable")]
    [Arguments("non-equipment")]
    [Arguments("wrong-dye")]
    [Arguments("reserved")]
    [Arguments("equipped")]
    public async Task Dye_InvalidTarget_RejectsWithoutConsumption(string rejection)
    {
        var equipment = AddEquipment(1, AppearanceTemplate(200, 1));
        var dye = AddItem(2, new ItemTemplate { Id = 100, MaxCount = 100, CategoryId = 33, UseSkillId = 22727 }, 2);
        using var reservation = new TradeReservation();
        Item target = equipment;
        switch (rejection)
        {
            case "non-dyeable": SetField(ItemManager.Instance, "_defaultDyeIds", new Dictionary<uint, uint>()); break;
            case "non-equipment": target = dye; break;
            case "wrong-dye": dye.Template.CategoryId = 1; break;
            case "reserved": reservation.TryReserve(equipment, 1); break;
            case "equipped":
                _bag.Items.Remove(equipment);
                equipment._holdingContainer = _owner.Inventory.Equipment;
                equipment.SlotType = SlotType.Equipment;
                _owner.Inventory.Equipment.Items.Add(equipment);
                break;
        }
        var skill = DyeSkill();
        var checkpoint = false;
        skill.CommitLaborBatch = (_, _) => checkpoint = true;
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            Dye(target, dye, skill);
            if (!skill.Cancelled)
                SkillLaborBatch.Current.Inventory.TryConsume(_bag, dye, 1);
        });
        await Assert.That(result).IsFalse();
        await Assert.That(checkpoint).IsFalse();
        await Assert.That(equipment.DyeItemId).IsEqualTo(0U);
        await Assert.That(dye.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Dye_ZeroLaborSkill_CommitsSourceAndSavedColorTogether(bool commit)
    {
        var equipment = AddEquipment(1, AppearanceTemplate(200, 1));
        equipment.DyeItemId = 50;
        equipment.IsDirty = false;
        var dye = AddItem(2, new ItemTemplate
        {
            Id = 100, MaxCount = 100, CategoryId = 33, UseSkillId = 22727, UseSkillAsReagent = true
        }, 2);
        var skill = DyeSkill();
        var checkpoint = false;
        skill.CommitLaborBatch = (_, _) => { checkpoint = equipment.DyeItemId == 100 && dye.Count == 1; return commit; };
        skill.ApplyEffects(_owner, new SkillItem(_owner.ObjId, dye.Id, dye.TemplateId), _owner,
            new SkillCastItemTarget { Type = SkillCastTargetType.Item, ObjId = _owner.ObjId, Id = equipment.Id }, null);
        await Assert.That(checkpoint).IsTrue();
        await Assert.That(skill.Cancelled).IsEqualTo(!commit);
        await Assert.That(dye.Count).IsEqualTo(commit ? 1 : 2);
        await Assert.That(equipment.DyeItemId).IsEqualTo(commit ? 100U : 50U);
        var details = new PacketStream();
        equipment.WriteDetails(details);
        details.Rollback();
        var restored = new EquipItem();
        restored.ReadDetails(details);
        await Assert.That(restored.DyeItemId).IsEqualTo(equipment.DyeItemId);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
    }

    private void Dye(Item target, Item dye, Skill skill) => new Dyeing().Execute(_owner,
        new SkillItem { ItemId = dye.Id, ItemTemplateId = dye.TemplateId }, _owner,
        new SkillCastItemTarget { Id = target.Id }, null, skill, null, DateTime.UtcNow, 0, 0, 0, 0);

    private static Skill DyeSkill() => new(new SkillTemplate
    {
        Id = 22727, TargetType = SkillTargetType.Item,
        Effects = [new SkillEffect
        {
            Template = new SpecialEffect { SpecialEffectTypeId = SpecialType.Dyeing },
            StartLevel = 1, EndLevel = 99, Friendly = true, NonFriendly = true, Front = true, Back = true,
            Chance = 100, ApplicationMethod = SkillEffectApplicationMethod.Source, ConsumeItemCount = 1
        }]
    });

    private static EquipItemTemplate AppearanceTemplate(uint id, uint category) => new()
    {
        Id = id, MaxCount = 1,
        ItemLookConvert = new ItemLookConvert { Id = category, RequiredItemId = 100, RequiredItemCount = 2 }
    };

    private EquipItem AddEquipment(uint id, EquipItemTemplate template)
    {
        var item = new EquipItem(id, template, 1);
        Add(item);
        return item;
    }

    private Item AddItem(uint id, ItemTemplate template, int count)
    {
        var item = new ItemMock(id, template, count);
        Add(item);
        return item;
    }

    private void Add(Item item)
    {
        item.OwnerId = _owner.Id;
        item.SlotType = SlotType.Inventory;
        item.Slot = _bag.Items.Count;
        item._holdingContainer = _bag;
        _bag.Items.Add(item);
        _bag.UpdateFreeSlotCount();
        _items.Add(item.Id, item);
        _templates[item.TemplateId] = item.Template;
    }

    private void SetInstance<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
