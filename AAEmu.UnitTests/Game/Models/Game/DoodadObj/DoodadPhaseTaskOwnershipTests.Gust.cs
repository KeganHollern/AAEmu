using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Skills;
using GameTask = AAEmu.Game.Models.Tasks.Task;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

public sealed partial class DoodadPhaseTaskOwnershipTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Gust_SpawnedPhaseAppliesItsOwnPeriodicBuffAndRetiresOnlyItsOwnArea(bool expireBeforeTick)
    {
        var oldWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1 };
        try
        {
            SetUpWindEffects();
            SetUpWindDoodads();
            var world = CreateWindWorld();
            var source = new CharacterMock { Id = 10, ObjId = 100, IsVisible = true, ParentWorld = world };
            source.Transform.Local.SetPosition(32, 32, 100);
            var region = world.GetRegionByPos(source.Transform.World.Position);
            SetField(region, "_neighbors", new[] { region });
            void Place(GameObject obj, float x)
            {
                obj.ParentWorld = world;
                obj.Transform.Local.SetPosition(x, 32, 100);
                world.AddObject(obj);
                region.AddObject(obj);
                obj.Region = region;
            }
            Place(source, 32);
            var neighbor = CreateOwner();
            neighbor.ObjId = 200;
            neighbor.IsVisible = true;
            Place(neighbor, 29);
            var near = new CharacterMock { Id = 11, ObjId = 101 };
            var outside = new CharacterMock { Id = 12, ObjId = 102 };
            Place(near, 36);
            Place(outside, 26);
            var above = new CharacterMock { Id = 13, ObjId = 103 };
            Place(above, 32);
            above.Transform.Local.SetHeight(107);
            var participants = new[] { source, near, outside, above };
            var visible = new List<uint>();
            world.DoodadPhaseChanged += (doodad, _) =>
            {
                if (world.GetDoodad(doodad.ObjId) == doodad && doodad.IsVisible)
                    visible.Add(doodad.TemplateId);
            };
            var before = DateTime.UtcNow;
            CastWind(source);
            var after = DateTime.UtcNow;
            await Assert.That(world.GetAllDoodads().Count).IsEqualTo(1);
            var publications = ScheduledTasks();
            await Assert.That(publications.Length).IsEqualTo(2);
            source.Transform.Local.SetPosition(32, 34, 100);
            foreach (var publication in publications)
            {
                await Assert.That(publication.TriggerTime >= before.AddSeconds(1) && publication.TriggerTime <= after.AddSeconds(1)).IsTrue();
                publication.Execute();
            }
            await Assert.That(visible.Order()).IsEquivalentTo(new uint[] { 679, 1203 });
            var owner = world.GetAllDoodads().Single(d => d.TemplateId == 679);
            var visual = world.GetAllDoodads().Single(d => d.TemplateId == 1203);
            await Assert.That(owner.Transform.World.Position).IsEqualTo(new Vector3(32, 33, 100));
            await Assert.That(visual.Transform.World.Position).IsEqualTo(owner.Transform.World.Position);
            await Assert.That(visual.AttachAreaTriggers.Single().Owner).IsSameReferenceAs(visual);
            var trigger = owner.AttachAreaTriggers.Single();
            await Assert.That(trigger.Caster).IsSameReferenceAs(source);
            await Assert.That(trigger.Owner).IsSameReferenceAs(owner);
            await Assert.That(neighbor.AttachAreaTriggers).IsEmpty();
            var task = owner.FuncTask;
            if (!expireBeforeTick)
            {
                AreaTriggerManager.Instance.Tick(TimeSpan.FromMilliseconds(200));
                var affected = participants.Where(character => character.Buffs.CheckBuff(613)).ToList();
                await Assert.That(affected.Select(c => c.ObjId)).IsEquivalentTo(new[] { source.ObjId, near.ObjId });
                foreach (var character in affected)
                {
                    var initial = character.Buffs.GetEffectFromBuffId(613);
                    await Assert.That(initial.Caster).IsSameReferenceAs(source);
                    await Assert.That(initial.Duration).IsEqualTo(300);
                    ScheduledTasks().OfType<DispelTask>().Single(task => task.Effect.Target == initial).Execute();
                    var rising = character.Buffs.GetEffectFromBuffId(413);
                    await Assert.That(rising.Duration).IsEqualTo(6000);
                    await Assert.That(rising.Template.SkillControllerId).IsEqualTo(5815u);
                    ScheduledTasks().OfType<DispelTask>().Single(task => task.Effect.Target == rising).Execute();
                    await Assert.That(character.Buffs.GetEffectFromBuffId(415).Duration).IsEqualTo(15000);
                    await Assert.That(character.Buffs.CheckBuffTag(132)).IsTrue();
                }
            }
            task.Execute();
            AreaTriggerManager.Instance.Tick(TimeSpan.FromMilliseconds(200));
            await Assert.That(participants.Any(character => character.Buffs.CheckBuff(613))).IsFalse();
            await Assert.That(owner.AttachAreaTriggers).IsEmpty();
            await Assert.That(world.GetDoodad(owner.ObjId)).IsNull();
            await Assert.That(world.GetDoodad(visual.ObjId)).IsSameReferenceAs(visual);
            visual.FuncTask.Execute();
            await Assert.That(world.GetDoodad(visual.ObjId)).IsNull();
            await Assert.That(world.GetDoodad(neighbor.ObjId)).IsSameReferenceAs(neighbor);
        }
        finally
        {
            AppConfiguration.Instance.World = oldWorldConfig;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Gust_DelayedCastCannotPublishAfterCharacterOrWorldReplacement(bool replaceWorld)
    {
        SetUpWindEffects();
        var world = CreateWindWorld();
        SetUpWindDoodads();
        var caster = new CharacterMock { Id = 10, ObjId = 100, ParentWorld = world };
        world.AddObject(caster);
        CastWind(caster);
        var publications = ScheduledTasks();
        await Assert.That(publications.Length).IsEqualTo(2);
        if (replaceWorld)
            CreateWindWorld(); // A fresh world now owns the original instance ID.
        else
        {
            world.RemoveObject(caster);
            world.AddObject(new CharacterMock { Id = caster.Id, ObjId = caster.ObjId, ParentWorld = world });
        }
        foreach (var task in publications)
            task.Execute();
        await Assert.That(world.GetAllDoodads()).IsEmpty();
    }

    private static GameTask[] ScheduledTasks() =>
        ((ConcurrentDictionary<uint, GameTask>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(TaskManager.Instance)!).Values.ToArray();

    private static void CastWind(CharacterMock caster)
    {
        var skill = new Skill(new SkillTemplate { Id = 11592 });
        foreach (var id in new[] { 679, 1203 })
            new SpawnDoodad().Execute(caster, new SkillCasterUnit(caster.ObjId), caster, new SkillCastUnitTarget(caster.ObjId),
                null, skill, null, DateTime.UtcNow, id, 1000, 0, 0);
    }

    private void SetUpWindEffects()
    {
        var buff = new BuffTemplate { Id = 613, Duration = 300, StackRule = BuffStackRule.Refresh, MaxStack = 1, Kind = BuffKind.Good };
        var lift = new BuffTemplate { Id = 413, Duration = 6000, StackRule = BuffStackRule.Refresh, MaxStack = 1, SkillControllerId = 5815, Kind = BuffKind.Good };
        var recovery = new BuffTemplate { Id = 415, Duration = 15000, StackRule = BuffStackRule.Refresh, MaxStack = 1, Kind = BuffKind.Bad, ImmuneBuffTagId = 98 };
        var skills = new SkillManager(null, null);
        SetField(skills, "_buffs", new Dictionary<uint, BuffTemplate> { [613] = buff, [413] = lift, [415] = recovery });
        SetField(skills, "_buffTags", new Dictionary<uint, List<uint>> { [415] = [132] });
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>> { [132] = [415] });
        SetField(skills, "_buffTriggers", new Dictionary<uint, List<BuffTriggerTemplate>>
        {
            [613] = [new() { Id = 148, Kind = BuffEventTriggerKind.Timeout, EffectOnSource = true,
                Effect = new BuffEffect { Id = 902, Chance = 100, Stack = 1, Buff = lift } }],
            [413] = [new() { Id = 147, Kind = BuffEventTriggerKind.Timeout, EffectOnSource = true,
                Effect = new BuffEffect { Id = 901, Chance = 100, Stack = 1, Buff = recovery } }]
        });
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        ReplaceSingleton(models);
        var buffData = new BuffGameData();
        SetField(buffData, "_buffModifiers", new Dictionary<uint, List<BuffModifier>>());
        SetField(buffData, "_buffTolerances", new Dictionary<uint, BuffTolerance>());
        ReplaceSingleton(buffData);
        ReplaceSingleton(new MateGameData());
        ReplaceSingleton(new EffectTaskManager(TaskManager.Instance));
        SetField(skills, "_skillModifiers", new Dictionary<uint, List<SkillModifier>>());
        SetField(skills, "_combatBuffs", new Dictionary<uint, List<CombatBuffTemplate>>());
        SetField(skills, "_types", new Dictionary<uint, EffectType>
        {
            [1935] = new() { Id = 1935, ActualId = 584, Type = nameof(BuffEffect) }
        });
        SetField(skills, "_effects", new Dictionary<string, Dictionary<uint, EffectTemplate>>
        {
            [nameof(BuffEffect)] = new() { [584] = new BuffEffect { Id = 584, Chance = 100, Stack = 1, Buff = buff } }
        });
        ReplaceSingleton(skills);
        ReplaceSingleton(new DuelManager());
        ReplaceSingleton(new ZoneManager(null, null));
    }

    private static void SetUpWindDoodads()
    {
        var gust = new DoodadFuncClout
        {
            Id = 26, Duration = 500, Tick = 300, NextPhase = -1, UseOriginSource = true,
            TargetRelation = SkillTargetRelation.Any, AoeShapeId = 12, Effects = [1935]
        };
        var squall = new DoodadFuncClout
        {
            Id = 105, Duration = 6500, NextPhase = -1, UseOriginSource = true,
            TargetRelation = SkillTargetRelation.Any, AoeShapeId = 58, Effects = []
        };
        SetField(DoodadManager.Instance, "_templates", new Dictionary<uint, DoodadTemplate>
        {
            [679] = new() { Id = 679, FuncGroups = [new() { Id = 456, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }] },
            [1203] = new() { Id = 1203, FuncGroups = [new() { Id = 1817, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }] }
        });
        SetField(DoodadManager.Instance, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [456] = [new() { GroupId = 456, FuncId = 26, FuncType = nameof(DoodadFuncClout) }],
            [1817] = [new() { GroupId = 1817, FuncId = 105, FuncType = nameof(DoodadFuncClout) }]
        });
        SetField(DoodadManager.Instance, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            [nameof(DoodadFuncClout)] = new() { [26] = gust, [105] = squall }
        });
        var shapes = (ConcurrentDictionary<uint, AreaShape>)typeof(WorldManager)
            .GetField("_areaShapes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(WorldManager.Instance)!;
        shapes[12] = new AreaShape { Id = 12, Type = AreaShapeType.Sphere, Value1 = 5, Value2 = 5000 };
        shapes[58] = new AreaShape { Id = 58, Type = AreaShapeType.Sphere, Value1 = 10, Value2 = 10000 };
    }

    private static WorldInstance CreateWindWorld()
    {
        var size = WorldManager.SECTORS_PER_CELL;
        var template = new WorldTemplate { Id = 1, CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[size, size] };
        var world = new WorldInstance(template, 0, true, 1) { Regions = new Region[size, size] };
        SetField(WorldManager.Instance, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(
            new Dictionary<uint, WorldInstance> { [world.Id] = world }));
        return world;
    }
}
