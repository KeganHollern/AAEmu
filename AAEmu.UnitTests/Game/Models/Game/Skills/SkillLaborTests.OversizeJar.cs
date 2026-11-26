using System.Collections.Concurrent;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    private const uint JarSkill = 16855;

    private (Doodad Jar, SkillTemplate Skill) PrepareOversizeJar()
    {
        SetInstance(new TaskManager(null));
        SetInstance(new SkillRequirementsGameData());
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new ZoneManager(null, null));
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        SetInstance(models);
        SetInstance(new NpcGameData());
        SetInstance(new GameScheduleManager(null, TimeProvider.System));
        var requirements = new List<UnitReqs>();
        foreach (var itemId in new uint[] { 21175, 21176 })
            requirements.Add(new UnitReqs { OwnerId = JarSkill, OwnerType = "Skill", KindType = UnitReqsKindType.OwnItem, Value1 = itemId });
        SetField(UnitRequirementsGameData.Instance, "<_unitReqsByOwnerType>k__BackingField",
            new Dictionary<string, List<UnitReqs>> { ["Skill"] = requirements });

        var manager = new DoodadManager(null, null, null, null, null);
        var fakeUse = new DoodadFuncFakeUse { Id = 1855, FakeSkillId = JarSkill };
        SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> {
            [nameof(DoodadFuncFakeUse)] = new() { [1855] = fakeUse }
        });
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>> {
            [10535] = [new() { GroupId = 10535, FuncId = 1855, FuncType = nameof(DoodadFuncFakeUse), NextPhase = 10615 }]
        });
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        SetInstance(manager);
        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        var worlds = new WorldManager(null, null, null, null, null);
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });
        SetInstance(worlds);
        _owner.ParentWorld = world;
        world.SpawnManager = new SpawnManager(world);
        var jar = new Doodad { ObjId = 801, TemplateId = 4285, Template = new DoodadTemplate { Id = 4285 },
            FuncGroupId = 10535, ParentWorld = world };
        world.AddObject(jar);
        var template = new SkillTemplate
        {
            Id = JarSkill, TargetType = SkillTargetType.Doodad, MaxRange = 4, CastingTime = 5000,
            Effects = [
                new SkillEffect { EffectId = 16920, Chance = 100, EndLevel = 99, ApplicationMethod = SkillEffectApplicationMethod.Target,
                    Template = new InteractionEffect { Id = 2235, WorldInteraction = WorldInteractionType.Use, DoodadId = 4285 } },
                new SkillEffect { EffectId = 16924, Chance = 100, EndLevel = 99, ApplicationMethod = SkillEffectApplicationMethod.Target,
                    Template = new NpcSpawnerSpawnEffect { Id = 298, SpawnerId = 61949 } }
            ]
        };
        return (jar, template);
    }

    private Item PutHeartInBag(uint templateId, int slot)
    {
        var item = new Item((ulong)slot + 2, new ItemTemplate { Id = templateId, MaxCount = 1 }, 1)
        {
            Slot = slot, SlotType = SlotType.Inventory, OwnerId = _owner.Id, _holdingContainer = _owner.Inventory.Bag
        };
        _items.Add(item.Id, item);
        _owner.Inventory.Bag.Items.Add(item);
        _owner.Inventory.Bag.UpdateFreeSlotCount();
        return item;
    }

    [Test]
    [Arguments(false, false, 21176u)]
    [Arguments(true, false, 21176u)]
    [Arguments(false, true, 21175u)]
    [Arguments(true, true, 0u)]
    public async Task OversizeJar_RefusesMissingHeartsBeforeMissingSpawner(bool krox, bool lerox, uint missingItem)
    {
        var (jar, template) = PrepareOversizeJar();
        if (krox) PutHeartInBag(21175, 1);
        if (lerox) PutHeartInBag(21176, 2);
        var originalItems = _owner.Inventory.Bag.Items.ToArray();
        // Even another effect with an available spawner cannot provide the missing ritual.
        template.Effects.Add(new SkillEffect { Template = new NpcSpawnerSpawnEffect { Id = 999, SpawnerId = 1183 } });
        jar.ParentWorld.SpawnManager.AddNpcSpawner(new NpcSpawner { UnitId = 1207, NpcSpawnerIds = [1183] });
        var cast = new Skill(template);
        var result = cast.Use(_owner, new SkillCasterUnit(_owner.ObjId), new SkillCastDoodadTarget { ObjId = jar.ObjId },
            null, true, out var detail);
        await Assert.That(result).IsEqualTo(missingItem != 0 ? SkillResultHelper.SkillResultErrorKeyToId(SkillResultKeys.skill_urk_own_item) : SkillResult.NotNow);
        await Assert.That(detail).IsEqualTo(missingItem);
        await Assert.That(_owner.SkillTask).IsNull();
        await Assert.That(jar.FuncGroupId).IsEqualTo(10535u);
        await Assert.That(_owner.Inventory.Bag.Items.SequenceEqual(originalItems)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OversizeJar_RechecksTheInstanceBindingBeforeFinishing(bool keepBinding)
    {
        var (jar, template) = PrepareOversizeJar();
        var krox = PutHeartInBag(21175, 1);
        var lerox = PutHeartInBag(21176, 2);
        var manager = jar.ParentWorld.SpawnManager;
        var spawner = new JarTestSpawner { UnitId = 1207, NpcSpawnerIds = [61949], StartInactive = true, Position = new() };
        manager.AddNpcSpawner(spawner);
        var caster = new SkillCasterUnit(_owner.ObjId);
        var target = new SkillCastDoodadTarget { ObjId = jar.ObjId };
        var cast = new Skill(template);
        await Assert.That(cast.Use(_owner, caster, target, null, true, out _)).IsEqualTo(SkillResult.Success);
        await Assert.That(_owner.SkillTask.Skill).IsSameReferenceAs(cast);
        if (!keepBinding)
            await Assert.That(manager.RemoveNpcEventSpawners(1)).IsTrue();
        cast.ApplyEffects(_owner, caster, jar, target, null);
        await Assert.That(cast.Cancelled).IsEqualTo(!keepBinding);
        await Assert.That(jar.FuncGroupId).IsEqualTo(keepBinding ? 10615u : 10535u);
        await Assert.That(jar.ParentWorld.GetNpc(901)?.TemplateId).IsEqualTo(keepBinding ? 1207u : (uint?)null);
        await Assert.That(_owner.Inventory.Bag.Items.Contains(krox) && _owner.Inventory.Bag.Items.Contains(lerox)).IsTrue();
    }

    [Test]
    public async Task OversizeJar_DoesNotChangeTheOtherJarCast()
    {
        var (jar, template) = PrepareOversizeJar();
        jar.TemplateId = jar.Template.Id = 4283;
        jar.FuncGroupId = 10533;
        template.Id = 16852;
        template.Effects[1].Template = new NpcSpawnerSpawnEffect { Id = 296, SpawnerId = 61947 };
        var cast = new Skill(template);
        await Assert.That(cast.Use(_owner, new SkillCasterUnit(_owner.ObjId), new SkillCastDoodadTarget { ObjId = jar.ObjId },
            null, true, out _)).IsEqualTo(SkillResult.Success);
        await Assert.That(_owner.SkillTask.Skill).IsSameReferenceAs(cast);
    }

    private sealed class JarTestSpawner : NpcSpawner
    {
        protected override List<Npc> SpawnNpcDefinition(NpcSpawnerNpc definition)
        {
            var npc = new Npc { ObjId = 901, TemplateId = definition.MemberId, Hp = 100, Spawner = this, ParentWorld = ParentWorld };
            ParentWorld.AddObject(npc);
            return [npc];
        }
    }
}
