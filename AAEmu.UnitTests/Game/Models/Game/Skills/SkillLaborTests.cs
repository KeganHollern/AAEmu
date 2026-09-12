using System.Reflection;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Plots.Type;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed partial class SkillLaborTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private readonly Dictionary<uint, SkillReagent> _reagents = [];
    private readonly Dictionary<uint, SkillProduct> _products = [];
    private readonly Dictionary<uint, EffectTemplate> _effects = [];
    private readonly Dictionary<ulong, Item> _items = [];
    private ExecutionCharacter _owner;
    private Item _material;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new AccountManager(null, null, TimeProvider.System));
        var formulas = new FormulaManager();
        SetField(formulas, "_formulas", new Dictionary<uint, AAEmu.Game.Models.Game.Formulas.Formula>());
        SetInstance(formulas);
        var skills = new SkillManager(null, null);
        SetField(skills, "_skillTags", new Dictionary<uint, List<uint>>());
        SetField(skills, "_skillReagents", _reagents);
        SetField(skills, "_skillProducts", _products);
        SetField(skills, "_effects", new Dictionary<string, Dictionary<uint, EffectTemplate>> { ["Probe"] = _effects });
        SetInstance(skills);
        SetInstance(new UnitRequirementsGameData());
        SetInstance(new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        var ids = Mock.Of<IItemIdManager>();
        uint next = 1000;
        ids.GetNextId().Returns(() => next++);
        var itemManager = new ItemManager(skills, ids.Object, Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object, Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        SetInstance(itemManager);
        var templates = new Dictionary<uint, ItemTemplate>
        {
            [100] = new() { Id = 100, MaxCount = 100, BindType = ItemBindType.Normal, UseSkillId = 50 },
            [200] = new() { Id = 200, MaxCount = 100, BindType = ItemBindType.Normal }
        };
        SetField(itemManager, "_allItems", _items);
        SetField(itemManager, "_templates", templates);
        SetField(itemManager, "_removedItems", new List<ulong>());
        _owner = new ExecutionCharacter { Id = 7, ObjId = 70, Name = "Caster", Money = 100, NumInventorySlots = 10 };
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate) continue;
            var container = new ItemContainer(7, type, false, _owner)
            { Owner = _owner, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        SetField(itemManager, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
        _owner.Craft = new CharacterCraft(_owner);
        _owner.InitializeLaborCache(20, DateTime.UtcNow);
        _material = new Item
        {
            Id = 1,
            TemplateId = 100,
            Template = templates[100],
            Count = 3,
            OwnerId = 7,
            SlotType = SlotType.Inventory,
            Slot = 0,
            _holdingContainer = _owner.Inventory.Bag
        };
        _items.Add(_material.Id, _material);
        _owner.Inventory.Bag.Items.Add(_material);
        _owner.Inventory.Bag.UpdateFreeSlotCount();
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousInstances)
            field.SetValue(null, previous);
    }

    [Test]
    public async Task Use_ZeroLaborRejectsBeforeAnySkillEffect()
    {
        _owner.InitializeLaborCache(0, DateTime.UtcNow);
        var skill = NewSkill();
        await Assert.That(skill.Use(_owner, null, null, null, true, out _)).IsEqualTo(SkillResult.NeedLaborPower);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(5000)]
    [Arguments(0)]
    public async Task Use_CraftDurationAppliesProficiencyOnceAndSchedulesThePacketDuration(int skillBase)
    {
        SetInstance(new TaskManager(null));
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new SkillRequirementsGameData());
        SetInstance(new ZoneManager(null, null));
        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_owner, world);
        var units = (System.Collections.Concurrent.ConcurrentDictionary<uint, Unit>)typeof(WorldInstance)
            .GetField("_units", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        units[_owner.ObjId] = _owner;
        var skill = NewSkill();
        skill.Template.CastingTime = skillBase;
        skill.BaseCastingTime = CraftDuration.GetBaseMilliseconds(new Craft { Id = 4107, CastDelay = 15000 }, skill.Template);
        skill.CastTimeMultiplier = 0.8f;
        var before = DateTime.UtcNow;
        var result = skill.Use(_owner, new SkillCasterUnit(70), new SkillCastUnitTarget(70), null, true, out _);
        var after = DateTime.UtcNow;
        await Assert.That(result).IsEqualTo(SkillResult.Success);
        var packet = _owner.Packets.OfType<AAEmu.Game.Core.Packets.G2C.SCSkillStartedPacket>().Single();
        await Assert.That(packet.RealCastTimeDiv10).IsEqualTo((ushort)1200);
        await Assert.That(packet.BaseCastTimeDiv10).IsEqualTo((ushort)1200);
        await Assert.That(_owner.SkillTask.TriggerTime >= before.AddSeconds(12)).IsTrue();
        await Assert.That(_owner.SkillTask.TriggerTime <= after.AddSeconds(12)).IsTrue();
        await Assert.That(skill.Template.CastingTime).IsEqualTo(skillBase);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task VocationReward_JoinsTheLaborCheckpoint(bool succeeds)
    {
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => succeeds;
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
            _owner.ChangeGamePoints(GamePointKind.Vocation, 7));
        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(_owner.VocationPoint).IsEqualTo(succeeds ? 7 : 0);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(succeeds ? 10 : 20));
    }

    [Test]
    public async Task Completion_ChargesLaborWithProductsOnce()
    {
        Products();
        var commits = 0;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => { commits++; return true; };
        Apply(skill);
        Apply(skill);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)10);
        await Assert.That(_owner.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(200u);
        await Assert.That(_material.Count).IsEqualTo(0);
        await Assert.That(commits).IsEqualTo(1);
    }

    [Test]
    [Arguments("labor_lost")]
    [Arguments("full_bag")]
    [Arguments("failed_commit")]
    [Arguments("missing_material")]
    public async Task CompletionFailure_PreservesLaborMaterialsAndProducts(string reason)
    {
        Products();
        var skill = NewSkill();
        if (reason == "labor_lost") _owner.InitializeLaborCache(0, DateTime.UtcNow);
        if (reason == "full_bag") { _owner.Inventory.Bag.ContainerSize = 1; _owner.Inventory.Bag.UpdateFreeSlotCount(); }
        if (reason == "failed_commit") skill.CommitLaborBatch = (_, _) => false;
        if (reason == "missing_material") _material.Count = 2;
        var labor = _owner.LaborPower;
        var count = _material.Count;
        Apply(skill);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_owner.LaborPower).IsEqualTo(labor);
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(_material);
        await Assert.That(_material.Count).IsEqualTo(count);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AsyncPlotNode_LaborMarkerAndProductShareOneBatch(bool hasLabor)
    {
        _owner.InitializeLaborCache(hasLabor ? (short)20 : (short)0, DateTime.UtcNow);
        var skill = NewSkill();
        var node = new PlotNode { Event = new PlotEventTemplate { Id = 9, PlotId = 9 } };
        _effects[1] = new SpecialEffect { SpecialEffectTypeId = SpecialType.GainItem, Value1 = 200, Value2 = 1 };
        _effects[2] = new SpecialEffect { SpecialEffectTypeId = SpecialType.ConsumeLaborPower };
        foreach (var id in new uint[] { 1, 2 })
            node.Event.Effects.AddLast(new PlotEventEffect { ActualId = id, ActualType = "Probe",
                SourceId = PlotEffectSource.OriginalSource, TargetId = PlotEffectTarget.OriginalSource });
        skill.Template.Plot = new Plot { Tree = new PlotTree(9) { RootNode = node } };
        skill.Template.PlotOnly = true;
        var state = new PlotState(_owner, new SkillCasterUnit(70), _owner, new SkillCastUnitTarget(70), null, skill);
        var targets = new PlotTargetInfo(_owner, _owner);
        targets.EffectedTargets.Add(_owner);
        await Task.Run(() => node.Execute(state, targets));
        await Assert.That(_owner.LaborPower).IsEqualTo(hasLabor ? (short)10 : (short)0);
        await Assert.That(_owner.Inventory.Bag.Items.Any(item => item.TemplateId == 200)).IsEqualTo(hasLabor);
        await Assert.That(state.CancellationRequested()).IsEqualTo(!hasLabor);
    }

    [Test]
    public async Task ConcurrentCompletions_CannotSpendTheSameLaborTwice()
    {
        _owner.InitializeLaborCache(10, DateTime.UtcNow);
        _products.Add(1, new SkillProduct { Id = 1, SkillId = 50, ItemId = 200, Amount = 1 });
        var first = NewSkill();
        var second = NewSkill();
        await Task.WhenAll(Task.Run(() => Apply(first)), Task.Run(() => Apply(second)));
        await Assert.That(_owner.LaborPower).IsEqualTo((short)0);
        await Assert.That(_owner.Inventory.Bag.Items.Single(item => item.TemplateId == 200).Count).IsEqualTo(1);
        await Assert.That(first.Cancelled != second.Cancelled).IsTrue();
    }

    [Test]
    public async Task FailedBatch_RestoresDoodadAndDefersDeletion()
    {
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => false;
        var doodad = new AAEmu.Game.Models.Game.DoodadObj.Doodad { GrowthTime = DateTime.UtcNow };
        var growth = doodad.GrowthTime;
        var deleted = false;
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            SkillLaborBatch.Current.TrackDoodad(doodad);
            doodad.GrowthTime = growth.AddHours(1);
            SkillLaborBatch.Current.DeleteDoodad(doodad, () => deleted = true);
            _owner.Inventory.Bag.AcquireDefaultItem(ItemTaskType.SkillEffectGainItem, 200, 1);
        });
        await Assert.That(result).IsFalse();
        await Assert.That(deleted).IsFalse();
        await Assert.That(doodad.GrowthTime).IsEqualTo(growth);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(_material);
    }

    [Test]
    public async Task Completion_ConsumedSourceStillPublishesItemUse()
    {
        _material.Count = 1;
        _reagents.Add(1, new SkillReagent { Id = 1, SkillId = 50, ItemId = 100, Amount = 1 });
        var skill = NewSkill();
        var uses = 0;
        _owner.ConditionChance = true;
        _owner.Events.OnItemUse += (_, _) => uses++;
        skill.ApplyEffects(_owner, new SkillItem(70, _material.Id, _material.TemplateId),
            _owner, new SkillCastUnitTarget(70), null);
        await Assert.That(_material.Count).IsEqualTo(0);
        await Assert.That(uses).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Tempering_CommitsEquipmentFieldsWithLaborAndSourceOrRestoresAll(bool succeeds)
    {
        var equipment = new EquipItem(99, new EquipItemTemplate { Id = 300, MaxCount = 1 }, 1)
        {
            OwnerId = _owner.Id, SlotType = SlotType.Inventory, Slot = 1,
            _holdingContainer = _owner.Inventory.Bag, TemperPhysical = 103, TemperMagical = 104
        };
        _owner.Inventory.Bag.Items.Add(equipment);
        _items.Add(equipment.Id, equipment);
        SetField(ItemManager.Instance, "_itemCapScales", new Dictionary<uint, AAEmu.Game.Models.Game.Items.ItemCapScale>
        { [50] = new() { SkillId = 50, ScaleMin = 105, ScaleMax = 106 } });
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => succeeds;
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            new AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects.ItemCapScale().Execute(_owner,
                new SkillItem(70, _material.Id, _material.TemplateId), _owner,
                new SkillCastItemTarget { Id = equipment.Id }, new CastSkill(50, 0), skill, null,
                DateTime.UtcNow, 0, 0, 0, 0);
            _owner.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, _material.TemplateId, 1, _material);
        });
        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(equipment.TemperPhysical).IsEqualTo((ushort)(succeeds ? 105 : 103));
        await Assert.That(equipment.TemperMagical).IsEqualTo((ushort)(succeeds ? 105 : 104));
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(succeeds ? 10 : 20));
        await Assert.That(_material.Count).IsEqualTo(succeeds ? 2 : 3);
    }

    private void Products()
    {
        _reagents.Add(1, new SkillReagent { Id = 1, SkillId = 50, ItemId = 100, Amount = 3 });
        _products.Add(1, new SkillProduct { Id = 1, SkillId = 50, ItemId = 200, Amount = 1 });
    }
    private static Skill NewSkill() => new(new SkillTemplate { Id = 50, TargetType = SkillTargetType.Self, ConsumeLaborPower = 10 })
        { CommitLaborBatch = (_, _) => true };
    private void Apply(Skill skill) => skill.ApplyEffects(_owner, new SkillCasterUnit(70), _owner, new SkillCastUnitTarget(70), null);
    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ProbeEffect(Action action) : EffectTemplate
    {
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time, CompressedGamePackets packetBuilder = null) => action();
    }

    private sealed class ExecutionCharacter : CharacterMock
    {
        public List<AAEmu.Game.Core.Network.Game.GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(AAEmu.Game.Core.Network.Game.GamePacket packet, bool self) => Packets.Add(packet);
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
        public override float CastTimeMul => 1;
        public override float GlobalCooldownMul => 100;
        public override double ApplySkillModifiers(Skill skill, SkillAttribute attribute, double baseValue) => baseValue;
    }
}
