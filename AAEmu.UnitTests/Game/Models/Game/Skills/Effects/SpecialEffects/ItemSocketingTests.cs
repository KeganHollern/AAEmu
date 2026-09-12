using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Game.Models.Game.Items;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

[NotInParallel]
public sealed class ItemSocketingTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private SocketCharacter _owner;
    private EquipItem _equipment;
    private Item _gem;
    private ItemManager _manager;
    private SkillItem _source;
    private SkillCastItemTarget _target;

    [Before(Test)]
    public void SetUp()
    {
        var skills = new SkillManager(null, null);
        SetField(skills, "_skillReagents", new Dictionary<uint, SkillReagent>());
        SetField(skills, "_skillProducts", new Dictionary<uint, SkillProduct>());
        SetInstance(skills);
        SetInstance(new UnitRequirementsGameData());
        SetInstance(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _manager = new ItemManager(skills, Mock.Of<IItemIdManager>().Object, Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object, Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        SetInstance(_manager);
        typeof(ItemManager).GetProperty(nameof(ItemManager.SocketingRules))!.SetValue(_manager, ItemSocketingRulesTests.LoadRules());
        SetField(_manager, "_socketChance", new Dictionary<uint, uint> { [1] = 10000, [2] = 5000, [3] = 5000, [4] = 5000 });
        SetField(_manager, "_removedItems", new List<ulong>());
        _owner = new SocketCharacter { Id = 7, ObjId = 70, Name = "Socket", Money = 100, Mp = 100, Level = 50, NumInventorySlots = 10, ConditionChance = true };
        _owner.Actability = new CharacterActability(_owner);
        typeof(Character).GetField("_laborPower", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_owner, (short)100);
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate) continue;
            var container = new ItemContainer(7, type, false, _owner) { Owner = _owner, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        SetField(_manager, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
        _equipment = ItemSocketingRulesTests.Equipment();
        _equipment.Id = 1;
        _equipment.TemplateId = 100;
        _equipment.OwnerId = 7;
        _equipment.SlotType = SlotType.Inventory;
        _equipment._holdingContainer = _owner.Inventory.Bag;
        _gem = new Item
        {
            Id = 2, TemplateId = 30907, Count = 3, OwnerId = 7, SlotType = SlotType.Inventory, Slot = 1,
            Template = new ItemTemplate { Id = 30907, UseSkillId = 23728, UseSkillAsReagent = true, MaxCount = 100 },
            _holdingContainer = _owner.Inventory.Bag
        };
        SetField(_manager, "_allItems", new Dictionary<ulong, Item> { [1] = _equipment, [2] = _gem });
        SetField(_manager, "_templates", new Dictionary<uint, ItemTemplate> { [100] = _equipment.Template, [30907] = _gem.Template });
        _owner.Inventory.Bag.Items.AddRange([_equipment, _gem]);
        _owner.Inventory.Bag.UpdateFreeSlotCount();
        _source = new SkillItem(70, 2, 30907);
        _target = new SkillCastItemTarget { Type = SkillCastTargetType.Item, ObjId = 70, Id = 1 };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments("full")]
    [Arguments("group")]
    [Arguments("level")]
    public async Task Use_InvalidRequestStopsBeforeManaLaborMoneySourceOrGcd(string rejection)
    {
        MakeInvalid(rejection);
        var before = _equipment.GemIds.ToArray();
        var skill = NewSkill();
        var result = skill.Use(_owner, _source, _target, null, false, out _);
        await Assert.That(result).IsEqualTo(SkillResult.InvalidTarget);
        await AssertUnchanged(skill, before);
        await Assert.That(_owner.GlobalCooldown).IsEqualTo(default(DateTime));
    }

    [Test]
    [Arguments("full")]
    [Arguments("group")]
    [Arguments("level")]
    public async Task Cast_RechecksChangedTargetBeforeManaAndFinalConsumption(string rejection)
    {
        var skill = NewSkill();
        await Assert.That(ItemSocketing.ValidateSkill(_owner, _source, _target, skill)).IsTrue();
        MakeInvalid(rejection);
        var before = _equipment.GemIds.ToArray();
        skill.Cast(_owner, _source, _owner, _target, null);
        await AssertUnchanged(skill, before);
        await Assert.That(_owner.GlobalCooldown).IsEqualTo(default(DateTime));
    }

    [Test]
    [Arguments("full")]
    [Arguments("group")]
    [Arguments("level")]
    public async Task DelayedEffects_RecheckBeforeEffectRollAndItemUse(string rejection)
    {
        var skill = NewSkill();
        await Assert.That(ItemSocketing.ValidateSkill(_owner, _source, _target, skill)).IsTrue();
        MakeInvalid(rejection);
        var before = _equipment.GemIds.ToArray();
        var itemUseEvents = 0;
        _owner.Events.OnItemUse += (_, _) => itemUseEvents++;
        skill.ApplyEffects(_owner, _source, _owner, _target, null);
        skill.EndSkill(_owner);
        await AssertUnchanged(skill, before);
        await Assert.That(itemUseEvents).IsEqualTo(0);
    }

    [Test]
    [Arguments("full")]
    [Arguments("group")]
    [Arguments("level")]
    public async Task DirectEffect_RejectionDoesNotRollOrChangeAnyGem(string rejection)
    {
        MakeInvalid(rejection);
        var before = _equipment.GemIds.ToArray();
        var skill = NewSkill();
        var effect = new ControlledSocketing();
        _equipment.IsDirty = false;
        Execute(effect, skill);
        await Assert.That(effect.Rolls).IsEqualTo(0);
        await Assert.That(_equipment.IsDirty).IsFalse();
        await AssertUnchanged(skill, before);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("empty")]
    [Arguments("owner")]
    [Arguments("container")]
    [Arguments("template")]
    [Arguments("skill")]
    public async Task Source_RechecksInventoryIdentityAndPaymentSource(string change)
    {
        switch (change)
        {
            case "missing": _owner.Inventory.Bag.Items.Remove(_gem); break;
            case "empty": _gem.Count = 0; break;
            case "owner": _gem.OwnerId = 8; break;
            case "container": _gem._holdingContainer = null; break;
            case "template": _source.ItemTemplateId = 1; break;
            case "skill": _gem.Template.UseSkillId = 1; break;
        }
        var skill = NewSkill();
        var effect = new ControlledSocketing();
        Execute(effect, skill);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(effect.Rolls).IsEqualTo(0);
        await Assert.That(_equipment.GemIds.All(id => id == 0)).IsTrue();
    }

    [Test]
    public async Task Cast_ReservedAssetsRejectBeforeAnyResourceUse()
    {
        using var reservation = new TradeReservation();
        await Assert.That(reservation.TryReserve(_gem, 1)).IsTrue();
        var skill = NewSkill();
        skill.Cast(_owner, _source, _owner, _target, null);
        await AssertUnchanged(skill, new uint[7]);
    }

    [Test]
    [Arguments(0, true)]
    [Arguments(9999, false)]
    public async Task ValidRoll_PreservesSuccessAndFailureBehavior(int roll, bool success)
    {
        _equipment.GemIds[1] = 30918; // A free hole must not overwrite another gem.
        var effect = new ControlledSocketing { Roll = roll };
        var skill = NewSkill();
        Execute(effect, skill);
        await Assert.That(effect.Rolls).IsEqualTo(1);
        await Assert.That(skill.Cancelled).IsFalse();
        await Assert.That(_equipment.GemIds.SequenceEqual(success ? new uint[] { 30907, 30918, 0, 0, 0, 0, 0 } : new uint[7])).IsTrue();
        // Source consumption belongs to Skill.ApplyEffects, not the special effect.
        await Assert.That(_gem.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ValidEffects_ConsumeExactlyOneSourceAndKeepSevenSerializedSlots(bool success)
    {
        _equipment.GemIds[0] = 30918;
        _equipment.IsDirty = false;
        SetField(_manager, "_socketChance", new Dictionary<uint, uint> { [2] = success ? 10000u : 0u });
        var skill = NewSkill();
        skill.ApplyEffects(_owner, _source, _owner, _target, null);
        await Assert.That(skill.Cancelled).IsFalse();
        await Assert.That(_gem.Count).IsEqualTo(2);
        await Assert.That(_equipment.GemIds.SequenceEqual(success ? new uint[] { 30918, 30907, 0, 0, 0, 0, 0 } : new uint[7])).IsTrue();
        await Assert.That(_equipment.IsDirty).IsTrue();
        var bytes = new PacketStream();
        _equipment.WriteDetails(bytes);
        await Assert.That(bytes.Count).IsEqualTo(55);
        bytes.Rollback();
        var reloaded = new EquipItem();
        reloaded.ReadDetails(bytes);
        await Assert.That(reloaded.GemIds.SequenceEqual(_equipment.GemIds)).IsTrue();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Dawnstone_RemovesGemsWithoutAnInstallRollAndMarksTheResultDirty()
    {
        _equipment.GemIds[0] = 30918;
        _equipment.Grade = 0;
        _equipment.IsDirty = false;
        _gem.TemplateId = Item.DawnStone;
        _gem.Template.Id = Item.DawnStone;
        _gem.Template.UseSkillId = 23729;
        _source.ItemTemplateId = Item.DawnStone;
        var skill = NewSkill();
        skill.Template.Id = 23729;
        var effect = new ControlledSocketing();
        Execute(effect, skill);
        await Assert.That(skill.Cancelled).IsFalse();
        await Assert.That(effect.Rolls).IsEqualTo(0);
        await Assert.That(_equipment.GemIds.All(id => id == 0)).IsTrue();
        await Assert.That(_equipment.IsDirty).IsTrue();
    }

    [Test]
    public async Task ConcurrentEffects_LastLegalSlotConsumesOnlyOneGem()
    {
        _equipment.GemIds = [30918, 30918, 30918, 0, 0, 0, 0];
        SetField(_manager, "_socketChance", new Dictionary<uint, uint> { [4] = 10000 });
        var first = NewSkill();
        var second = NewSkill();
        var protectedConsumption = false;
        _owner.Events.OnItemUse += (_, _) => protectedConsumption =
            Monitor.IsEntered(SaveManager.PersistenceSyncRoot) && Skill.IsExecuting(_owner);
        await Task.WhenAll(
            Task.Run(() => first.ApplyEffects(_owner, _source, _owner, _target, null)),
            Task.Run(() => second.ApplyEffects(_owner, _source, _owner, _target, null)));
        await Assert.That(first.Cancelled != second.Cancelled).IsTrue();
        await Assert.That(_equipment.GemIds.Count(id => id != 0)).IsEqualTo(4);
        await Assert.That(_gem.Count).IsEqualTo(2);
        await Assert.That(protectedConsumption).IsTrue();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    private async Task AssertUnchanged(Skill skill, uint[] gems)
    {
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_equipment.GemIds.SequenceEqual(gems)).IsTrue();
        await Assert.That(_gem.Count).IsEqualTo(3);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(_owner.Mp).IsEqualTo(100);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)100);
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    private void MakeInvalid(string rejection)
    {
        switch (rejection)
        {
            case "full": _equipment.GemIds = [30918, 30918, 30918, 30918, 0, 0, 0]; break;
            case "group": ((WeaponTemplate)_equipment.Template).HoldableTemplate.SlotTypeId = 16; break;
            case "level": _equipment.Template.Level = 19; break;
        }
    }

    private void Execute(ItemSocketing effect, Skill skill) => effect.Execute(_owner, _source, _owner, _target,
        new CastSkill(skill.Id, 0), skill, null, DateTime.UtcNow, 1, 0, 0, 0);

    private static Skill NewSkill() => new(new SkillTemplate
    {
        Id = 23728, TargetType = SkillTargetType.Item, ManaCost = 10, ConsumeLaborPower = 0, CustomGcd = 1000,
        Effects = [new SkillEffect
        {
            Template = new SpecialEffect { SpecialEffectTypeId = SpecialType.ItemSocketing, Value1 = 1 },
            StartLevel = 1, EndLevel = 99, Friendly = true, NonFriendly = true, Front = true, Back = true,
            Chance = 100, ApplicationMethod = SkillEffectApplicationMethod.Source, ConsumeItemCount = 1
        }]
    });

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ControlledSocketing : ItemSocketing
    {
        public int Rolls { get; private set; }
        public int Roll { get; init; }
        protected override int RollSocketChance() { Rolls++; return Roll; }
    }

    private sealed class SocketCharacter : CharacterMock
    {
        public override float CastTimeMul => 1;
        public override float GlobalCooldownMul => 100;
        public override double ApplySkillModifiers(Skill skill, SkillAttribute attribute, double baseValue) => baseValue;
    }
}
