using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(4, 1, true, ErrorMessageType.NoErrorMessage)]
    [Arguments(3, 0, true, ErrorMessageType.NoErrorMessage)]
    [Arguments(4, 100, true, ErrorMessageType.NotEnoughSpace)]
    [Arguments(2, 1, true, ErrorMessageType.NotEnoughRequiredItem)]
    [Arguments(3, 0, false, ErrorMessageType.InternalError)]
    public async Task LumberCraft_FullBagSettlesMaterialsProductAndLaborTogether(
        int logs, int lumber, bool commitSucceeds, ErrorMessageType expectedError)
    {
        const uint logId = 8017, lumberId = 8337;
        var templates = new Dictionary<uint, ItemTemplate>
        {
            [logId] = new() { Id = logId, MaxCount = 1000 },
            [lumberId] = new() { Id = lumberId, MaxCount = 100 },
            [100] = _material.Template
        };
        SetField(ItemManager.Instance, "_templates", templates);
        var bag = _owner.Inventory.Bag;
        bag.ContainerSize = 2;
        _material.TemplateId = logId;
        _material.Template = templates[logId];
        _material.Count = logs;
        var neighborId = lumber > 0 ? lumberId : 100u;
        var neighbor = new Item(2, templates[neighborId], Math.Max(1, lumber))
        {
            OwnerId = _owner.Id, SlotType = SlotType.Inventory, Slot = 1, _holdingContainer = bag
        };
        bag.Items.Add(neighbor);
        _items.Add(neighbor.Id, neighbor);
        bag.UpdateFreeSlotCount();
        await Assert.That(bag.FreeSlotCount).IsEqualTo(0);

        var carpentry = SetUpCarpentryRewards();
        var recipe = new Craft
        {
            Id = 83, SkillId = 14618, ReqDoodadId = 558,
            CraftMaterials = [new CraftMaterial { ItemId = logId, Amount = 3 }],
            CraftProducts = [new CraftProduct { ItemId = lumberId, Amount = 1 }]
        };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(CharacterCraft).GetProperty("CurrentCraft", flags)!.SetValue(_owner.Craft, recipe);
        typeof(CharacterCraft).GetProperty("Count", flags)!.SetValue(_owner.Craft, 1);
        _owner.Craft.IsCrafting = true;
        var commitCount = 0;
        var skill = new Skill(new SkillTemplate
        {
            Id = recipe.SkillId, TargetType = SkillTargetType.Doodad, ConsumeLaborPower = 5, ActabilityGroupId = 18,
            Effects = [new SkillEffect
            {
                ApplicationMethod = SkillEffectApplicationMethod.Target, Chance = 100, EndLevel = 99,
                Template = new CraftEffect { WorldInteraction = WorldInteractionType.LumberProcess }
            }]
        });
        skill.CommitLaborBatch = (_, _) =>
        {
            commitCount++;
            return commitSucceeds;
        };
        var sawmill = PlaceSawmill(recipe.ReqDoodadId);
        typeof(CharacterCraft).GetProperty("DoodadId", flags)!.SetValue(_owner.Craft, sawmill.ObjId);
        var session = new RankFeedbackSession();
        _owner.Connection = new GameConnection(session) { ActiveChar = _owner };
        var credits = new List<uint>();
        _owner.Events.OnCraft += (_, args) => credits.Add(args.CraftId);

        var previousWorld = AppConfiguration.Instance.World;
        try
        {
            AppConfiguration.Instance.World = new WorldConfig { ExpRate = 1 };
            skill.ApplyEffects(_owner, new SkillCasterUnit(_owner.ObjId), sawmill,
                new SkillCastDoodadTarget { ObjId = sawmill.ObjId }, null);
        }
        finally
        {
            AppConfiguration.Instance.World = previousWorld;
        }

        var errors = session.Packets
            .Where(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCErrorMsgPacket)
            .Select(packet => (ErrorMessageType)BitConverter.ToUInt16(packet, 10)).ToArray();
        var expectedSuccess = expectedError == ErrorMessageType.NoErrorMessage;
        await Assert.That(errors).IsEquivalentTo(expectedSuccess ? Array.Empty<ErrorMessageType>() : [expectedError]);
        await Assert.That(commitCount).IsEqualTo(expectedSuccess || !commitSucceeds ? 1 : 0);
        await Assert.That(skill.Cancelled).IsEqualTo(!expectedSuccess);
        await Assert.That(_owner.Craft.IsCrafting).IsFalse();
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(expectedSuccess ? 15 : 20));
        await Assert.That(_owner.ConsumedLaborPower).IsEqualTo(expectedSuccess ? 5 : 0);
        await Assert.That(_owner.Experience).IsEqualTo(expectedSuccess ? 50267 : 50000);
        await Assert.That(_owner.Money).IsEqualTo(100L);
        await Assert.That(carpentry.Point).IsEqualTo(expectedSuccess ? 5 : 0);
        await Assert.That(credits).IsEquivalentTo(expectedSuccess ? new[] { recipe.Id } : Array.Empty<uint>());
        await Assert.That(_material.Count).IsEqualTo(logs - (expectedSuccess ? 3 : 0));
        await Assert.That(bag.Items.Sum(item => item.TemplateId == lumberId ? item.Count : 0))
            .IsEqualTo(lumber + (expectedSuccess ? 1 : 0));
        await Assert.That(bag.Items.Single(item => item.Slot == 1)).IsSameReferenceAs(neighbor);
        if (expectedSuccess && logs == 3)
            await Assert.That(bag.Items.Single(item => item.Slot == 0).TemplateId).IsEqualTo(lumberId);
        else
            await Assert.That(bag.Items.Single(item => item.Slot == 0)).IsSameReferenceAs(_material);
    }

    private Actability SetUpCarpentryRewards()
    {
        SetInstance(new ExperienceModifierGameData());
        var experience = new ExperienceManager();
        var levels = Mock.Of<IExperienceLevelTemplateLoader>();
        levels.Load().Returns(Enumerable.Range(1, 52).Select(level =>
            new ExperienceLevelTemplate { Level = (byte)level, TotalExp = (level - 1) * 1000 }).ToArray());
        experience.Load(levels.Object, 52, 52);
        SetInstance(experience);
        SetField(FormulaManager.Instance, "<CalculationEngine>k__BackingField", new Jace.CalculationEngine());
        SetField(FormulaManager.Instance, "_formulas", new Dictionary<uint, Formula>
        {
            [(uint)FormulaKind.ExpByLaborPower] = new("((pc_level * 4.5 + 37.5) / 5) * labor_power")
        });
        _owner.Level = 51;
        typeof(Character).GetProperty(nameof(Character.Experience))!.SetValue(_owner, 50000);
        _owner.Ability1 = AbilityType.Fight;
        _owner.Ability2 = _owner.Ability3 = AbilityType.None;
        _owner.Abilities = new CharacterAbilities(_owner);
        var characters = new CharacterManager(null, null, null, null, null, null, null, null, null, null, null);
        SetField(characters, "_expertLimits", new Dictionary<int, ExpertLimit> { [0] = new() { UpLimit = 10000 } });
        SetInstance(characters);
        _owner.Actability = new CharacterActability(_owner);
        var carpentry = new Actability(new ActabilityTemplate { Id = 18 });
        _owner.Actability.Actabilities.Add(carpentry.Id, carpentry);
        return carpentry;
    }

    private Doodad PlaceSawmill(uint templateId)
    {
        var world = new WorldManager(null, null, null, null, null);
        SetField(world, "_worldInteractionGroups", new Dictionary<uint, WorldInteractionGroup>
        {
            [(uint)WorldInteractionType.LumberProcess] = WorldInteractionGroup.Craft
        });
        SetInstance(world);
        var doodads = new DoodadManager(null, null, null, null, null);
        SetField(doodads, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        SetField(doodads, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [1010] = [new() { FuncKey = 10439, GroupId = 1010, FuncId = 51, FuncType = nameof(DoodadFuncCraftPack), NextPhase = -1 }]
        });
        SetInstance(doodads);
        SetInstance(new ZoneManager(null, null));
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        var sawmill = new Doodad { ObjId = 80, TemplateId = templateId,
            Template = new DoodadTemplate { Id = templateId }, FuncGroupId = 1010 };
        var instance = new WorldInstance(new WorldTemplate { Id = 0 }, 0, true, 0);
        var parent = typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!;
        parent.SetValue(_owner, instance);
        parent.SetValue(sawmill, instance);
        instance.AddObject(sawmill);
        return sawmill;
    }
}
