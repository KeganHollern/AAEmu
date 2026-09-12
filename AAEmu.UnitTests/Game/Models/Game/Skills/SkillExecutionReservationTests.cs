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
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class SkillExecutionReservationTests
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
        var skills = new SkillManager(null, null);
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task Use_ReservedItemOrMoneyRejectsBeforeSkillWork(bool money)
    {
        using var reservation = Reserve(money);
        var skill = new Skill(); // Entry rejection must precede template/world access.
        var result = skill.Use(_owner, new SkillItem(70, 1, 100), null, null, true, out var value);
        await Assert.That(result).IsEqualTo(SkillResult.ItemLocked);
        await Assert.That(value).IsEqualTo(0U);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.Money).IsEqualTo(100L);
    }

    [Test]
    public async Task Use_OrdinaryFailureReleasesExecution()
    {
        var skill = NewSkill();
        var result = skill.Use(_owner, null, null, null, true, out _);
        await Assert.That(result).IsEqualTo(SkillResult.NoTarget);
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Use_ExceptionReleasesExecution()
    {
        Assert.Throws<NullReferenceException>(() => new Skill().Use(_owner, null, null, null, true, out _));
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DelayedEffects_ReservedAssetsRejectProductsAndClearMatchingCraft(bool money)
    {
        EconomicSkill();
        ArmCraft(50);
        using var reservation = Reserve(money);
        var skill = NewSkill();
        await Assert.That(_owner.SkillTask).IsNull();
        Apply(skill);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_items.Values.Single()).IsSameReferenceAs(_material);
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.Craft.IsCrafting).IsFalse();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Effects_ExecutionIncludesProductCallbacksAndFinalConsumption()
    {
        EconomicSkill();
        var observed = false;
        _owner.Events.OnItemGather += (_, _) => observed |= Skill.IsExecuting(_owner) && _material.Count == 3;
        Apply(NewSkill());
        await Assert.That(observed).IsTrue();
        await Assert.That(_owner.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(200U);
        await Assert.That(_material.Count).IsEqualTo(0);
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Effects_DestinationResultControlsLaterEffectsProductsItemUseAndSourceCounts(bool denied)
    {
        ConfigureDestinationRule();
        EconomicSkill(); // Synthetic product relation tests ordering, not a live compact exploit.
        var skill = NewSkill();
        var source = new SkillItem(70, _material.Id, _material.TemplateId);
        var laterEffects = 0;
        var itemUses = 0;
        _owner.ConditionChance = true;
        _owner.Events.OnItemUse += (_, _) => itemUses++;
        skill.Template.Effects.Add(Effect(() => ApplyFishingDestination(skill, source, denied)));
        skill.Template.Effects.Add(Effect(() => laterEffects++));

        await Assert.That(ZoneSkillRestrictions.CanApply(_owner, skill, source)).IsTrue();
        skill.ApplyEffects(_owner, source, _owner, new SkillCastUnitTarget(70), null);

        await Assert.That(skill.Cancelled).IsEqualTo(denied);
        await Assert.That(laterEffects).IsEqualTo(denied ? 0 : 1);
        await Assert.That(itemUses).IsEqualTo(denied ? 0 : 1);
        await Assert.That(_owner.Inventory.GetItemsCount(200)).IsEqualTo(denied ? 0 : 1);
        await Assert.That(_material.Count).IsEqualTo(denied ? 3 : 0);
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Plot_DestinationResultControlsSameNodeQueuedNodesItemUseAndSourceCounts(bool denied)
    {
        ConfigureDestinationRule();
        var skill = NewSkill();
        var source = new SkillItem(70, _material.Id, _material.TemplateId);
        var sameNodeEffects = 0;
        var childEffects = 0;
        var itemUses = 0;
        _owner.Events.OnItemUse += (_, _) => itemUses++;
        var plot = Plot(() => ApplyFishingDestination(skill, source, denied));
        _effects.Add(2, new ProbeEffect(() => sameNodeEffects++));
        plot.Tree.RootNode.Event.Effects.AddLast(new PlotEventEffect
        {
            ActualId = 2,
            ActualType = "Probe",
            SourceId = PlotEffectSource.OriginalSource,
            TargetId = PlotEffectTarget.OriginalSource
        });
        var child = Plot(() => childEffects++).Tree.RootNode;
        child.Parent = plot.Tree.RootNode;
        child.ParentNextEvent = new PlotNextEvent();
        plot.Tree.RootNode.Children.Add(child); // Both nodes enter the final execution queue before its flush.
        skill.Template.Plot = plot;

        await Assert.That(ZoneSkillRestrictions.CanApply(_owner, skill, source)).IsTrue();
        await plot.RunAsync(_owner, source, _owner, new SkillCastUnitTarget(70), null, skill);

        await Assert.That(skill.Cancelled).IsEqualTo(denied);
        await Assert.That(skill.ActivePlotState.CancellationRequested()).IsEqualTo(denied);
        await Assert.That(sameNodeEffects).IsEqualTo(denied ? 0 : 1);
        await Assert.That(childEffects).IsEqualTo(denied ? 0 : 1);
        await Assert.That(itemUses).IsEqualTo(denied ? 0 : 1);
        await Assert.That(_material.Count).IsEqualTo(3);
    }

    [Test]
    public async Task PlotEffect_DestinationDenialStopsOtherTargetsWithoutAnAttachedActiveState()
    {
        ConfigureDestinationRule();
        var skill = NewSkill();
        var source = new SkillItem(70, _material.Id, _material.TemplateId);
        var calls = 0;
        var plot = Plot(() => { calls++; ApplyFishingDestination(skill, source, true); });
        var state = new PlotState(_owner, source, _owner, new SkillCastUnitTarget(70), null, skill);
        var targets = new PlotTargetInfo(_owner, _owner) { EffectedTargets = [_owner, _owner] };
        byte flag = 0;

        plot.Tree.RootNode.Event.Effects.First.Value.ApplyEffect(state, targets, plot.Tree.RootNode.Event, ref flag);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(state.CancellationRequested()).IsTrue();
        await Assert.That(skill.Cancelled).IsTrue();
    }

    [Test]
    public async Task Effects_NestedExecutionKeepsOuterBusyWithoutHoldingPersistenceGate()
    {
        var observed = false;
        var outer = NewSkill();
        outer.Template.Effects.Add(Effect(() =>
        {
            var before = Skill.IsExecuting(_owner) && !Monitor.IsEntered(SaveManager.PersistenceSyncRoot);
            Apply(NewSkill(51));
            observed = before && Skill.IsExecuting(_owner);
        }));
        Apply(outer);
        await Assert.That(observed).IsTrue();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Effects_ExceptionReleasesExecution()
    {
        var skill = NewSkill();
        skill.Template.Effects.Add(Effect(() => throw new InvalidOperationException("effect failure")));
        Assert.Throws<InvalidOperationException>(() => Apply(skill));
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Effects_AlreadyCancelledDoesNotEnterOrTouchTemplate()
    {
        Apply(new Skill { Cancelled = true });
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
        await Assert.That(_material.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Stop_ClearsOnlyMatchingCraftAndCancelledEffectsCannotGrant()
    {
        EconomicSkill();
        ArmCraft(50);
        NewSkill(51).Stop(_owner);
        await Assert.That(_owner.Craft.IsCrafting).IsTrue();
        var skill = NewSkill();
        skill.Stop(_owner);
        Apply(skill);
        await Assert.That(_owner.Craft.IsCrafting).IsFalse();
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_items.Values.Single()).IsSameReferenceAs(_material);
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Plot_ReservationRejectsBeforeQueueing(bool money)
    {
        using var reservation = Reserve(money);
        var skill = NewSkill();
        await Schedule(skill);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(skill.ActivePlotState).IsNull();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Plot_CancelledBeforeQueuedWorkSkipsPlotAndReleasesExecution()
    {
        var skill = NewSkill();
        skill.Cancelled = true;
        await Schedule(skill); // A null plot would throw if queued work ran it.
        await Assert.That(skill.ActivePlotState).IsNull();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Plot_AsyncFailureReleasesExecution()
    {
        var skill = NewSkill();
        skill.Template.Plot = new Plot(); // RunAsync enters, then its missing tree fails.
        var failed = false;
        try { await Schedule(skill); }
        catch (NullReferenceException) { failed = true; }
        await Assert.That(failed).IsTrue();
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    [Test]
    public async Task Plot_OverlappingCompletionKeepsDelayedPlotBusyUntilCancellationEnds()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionWasOutsideGate = false;
        var delayed = NewSkill();
        delayed.Template.Plot = Plot(() =>
        {
            executionWasOutsideGate = Skill.IsExecuting(_owner) && !Monitor.IsEntered(SaveManager.PersistenceSyncRoot);
            started.TrySetResult();
        }, 10_000);
        var pending = Schedule(delayed);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(executionWasOutsideGate).IsTrue();
            await Assert.That(Skill.IsExecuting(_owner)).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            var second = NewSkill(51);
            second.Template.Plot = Plot(() => { });
            await Schedule(second).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(Skill.IsExecuting(_owner)).IsTrue();
            await Assert.That(_owner.ActivePlotState).IsNull();
        }
        finally
        {
            delayed.ActivePlotState?.RequestCancellation();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Assert.That(Skill.IsExecuting(_owner)).IsFalse();
    }

    private TradeReservation Reserve(bool money)
    {
        var reservation = new TradeReservation();
        if (!(money ? reservation.TryReserve(_owner, 1) : reservation.TryReserve(_material, 1)))
            throw new InvalidOperationException("Fixture reservation failed");
        return reservation;
    }

    private void EconomicSkill()
    {
        _reagents.Add(1, new SkillReagent { Id = 1, SkillId = 50, ItemId = 100, Amount = 3 });
        _products.Add(1, new SkillProduct { Id = 1, SkillId = 50, ItemId = 200, Amount = 1 });
    }

    private void ConfigureDestinationRule()
    {
        var zones = new ZoneManager(null, null);
        SetField(zones, "_zones", new Dictionary<uint, Zone>
        {
            [1000] = new() { ZoneKey = 1000, GroupId = 45 },
            [2000] = new() { ZoneKey = 2000, GroupId = 1 }
        });
        SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>());
        SetField(zones, "_bannedTagsByGroup", new Dictionary<uint, ZoneGroupBannedTag[]>
        {
            [45] = [new() { Id = 185, ZoneGroupId = 45, TagId = 1348 }]
        });
        SetInstance(zones);
        var tags = new TagsGameData();
        SetField(tags, "_tags", new Dictionary<TagsGameData.TagType, Dictionary<uint, HashSet<uint>>>
        {
            [TagsGameData.TagType.Skills] = new() { [1348] = [50] }
        });
        SetInstance(tags);
        SetInstance(new WorldManager(null, null, null, null, null));
        var template = new WorldTemplate
        {
            Id = 10,
            CellX = 1,
            CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        };
        template.ZoneKeyByRegions[0, 0] = 1000;
        template.ZoneKeyByRegions[2, 0] = 2000;
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_owner, new WorldInstance(template, 0, true, 0));
        _owner.Transform.Local.SetPosition(130, 10, 100);
    }

    [Test]
    public async Task DelayedItemSkill_SourceMovedBeforeEffects_DoesNotApplyOrConsume()
    {
        EconomicSkill();
        var skill = NewSkill();
        var source = new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId);
        var effects = 0;
        skill.Template.Effects.Add(Effect(() => effects++));
        _owner.Inventory.Bag.Items.Remove(_material);
        _material.OwnerId = 8;
        skill.ApplyEffects(_owner, source, _owner, new SkillCastUnitTarget(_owner.ObjId), null);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(effects).IsEqualTo(0);
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.Inventory.Bag.Items).IsEmpty();
    }

    private void ApplyFishingDestination(Skill skill, SkillItem source, bool denied)
    {
        var target = new BaseUnit();
        target.Transform.Local.SetPosition(new Vector3(denied ? 10 : 130, 10, 100));
        new FishingLoot().Execute(_owner, source, target, null, null, skill, null, DateTime.UtcNow, 0, 0, 0, 0);
    }

    private static Skill NewSkill(uint id = 50) => new(new SkillTemplate { Id = id, TargetType = SkillTargetType.Self });

    private void Apply(Skill skill) => skill.ApplyEffects(_owner, new SkillCasterUnit(70), _owner, new SkillCastUnitTarget(70), null);

    private Task Schedule(Skill skill) => (Task)typeof(Skill).GetMethod("SchedulePlot", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(skill, [_owner, new SkillCasterUnit(70), _owner, new SkillCastUnitTarget(70), null])!;

    private static SkillEffect Effect(Action action) => new()
    {
        Template = new ProbeEffect(action),
        EndLevel = byte.MaxValue,
        Chance = 100,
        ApplicationMethod = SkillEffectApplicationMethod.Source
    };

    private Plot Plot(Action action, int delay = 0)
    {
        var effectId = (uint)_effects.Count + 1;
        _effects.Add(effectId, new ProbeEffect(action));
        var evt = new PlotEventTemplate { Id = effectId, SourceUpdateMethodId = 1, TargetUpdateMethodId = 1 };
        evt.Effects.AddLast(new PlotEventEffect
        {
            ActualId = effectId,
            ActualType = "Probe",
            SourceId = PlotEffectSource.OriginalSource,
            TargetId = PlotEffectTarget.OriginalSource
        });
        var tree = new PlotTree(effectId) { RootNode = new PlotNode { Event = evt } };
        if (delay > 0)
            tree.RootNode.Children.Add(new PlotNode
            {
                Event = new PlotEventTemplate { Id = 100, SourceUpdateMethodId = 1, TargetUpdateMethodId = 1 },
                Parent = tree.RootNode,
                ParentNextEvent = new PlotNextEvent { Delay = delay }
            });
        return new Plot { Id = effectId, Tree = tree };
    }

    private void ArmCraft(uint skillId)
    {
        typeof(CharacterCraft).GetProperty("CurrentCraft", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_owner.Craft, new Craft { Id = 1, SkillId = skillId });
        _owner.Craft.IsCrafting = true;
    }

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
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
        public override float CastTimeMul => 1;
        public override float GlobalCooldownMul => 100;
        public override double ApplySkillModifiers(Skill skill, SkillAttribute attribute, double baseValue) => baseValue;
    }
}
