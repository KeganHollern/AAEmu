using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData("success", false)]
    [InlineData("checkpoint", false)]
    [InlineData("labor", false)]
    [InlineData("success", true)]
    public void SkillLabor_TriggeredBenefitAndChildLaborShareTheParentCommit(string outcome, bool nested)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var oldZones = SwapSingleton(new ZoneManager(null, null));
        var oldUnitRequirements = SwapSingleton(new UnitRequirementsGameData());
        var oldSkillRequirements = SwapSingleton(new SkillRequirementsGameData());
        var oldWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { ExpRate = 1, VocationRate = 1, ActabilityRate = 1 };
        try
        {
            var player = graph.Sender;
            player.ObjId = player.Id;
            player.Hp = 100;
            player.Mp = 100;
            player.Actability = new CharacterActability(player);
            var world = new WorldInstance(new WorldTemplate { Id = 1, Name = "paid_child" }, 0, true, 1);
            typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, world);
            ((ConcurrentDictionary<uint, Unit>)typeof(WorldInstance).GetField("_units", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!)[player.ObjId] = player;
            var labor = outcome == "labor" ? 1 : 20;
            player.InitializeLaborCache((short)labor, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},{labor}) ON DUPLICATE KEY UPDATE labor={labor}");
            var material = graph.AddItem(0);
            material.Count = 1;
            Assert.True(graph.Save.TryCommitEconomy([player]));
            // Exact child 17686 consumes 1 labor and grants the 20-minute buff 3645.
            var buff = new BuffTemplate { Id = 3645, Duration = 1200000, Kind = BuffKind.Good,
                SaveRuleId = BuffSaveRuleType.Normal, StackRule = BuffStackRule.Refresh, MaxStack = 1 };
            services.AddTemplate(buff);
            var child = new SkillTemplate { Id = 17686, ConsumeLaborPower = 1, ActabilityGroupId = 5,
                TargetType = SkillTargetType.Self, MaxRange = 6, AbilityLevel = 1 };
            child.Effects.Add(TriggeredEffect(new BuffEffect { Buff = buff, Chance = 100 }));
            services.AddSkill(child);
            var childId = child.Id;
            if (nested)
            {
                var middle = new SkillTemplate { Id = 990001, TargetType = SkillTargetType.Self, MaxRange = 6 };
                middle.Effects.Add(TriggeredEffect(new SpecialEffect { SpecialEffectTypeId = SpecialType.SkillUse, Value1 = (int)child.Id }));
                services.AddSkill(middle);
                childId = middle.Id;
            }
            var parent = new Skill(new SkillTemplate { Id = 17617, ConsumeLaborPower = 1, ActabilityGroupId = 19 });
            var checkpointCalls = 0;
            parent.CommitLaborBatch = (_, write) =>
            {
                checkpointCalls++;
                Assert.Null(player.Buffs.GetEffectFromBuffId(buff.Id));
                return graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
                {
                    write(context);
                    if (outcome == "checkpoint")
                        throw new InvalidOperationException("Forced child benefit checkpoint failure");
                });
            };
            Assert.True(Skill.CanSettleTriggeredEffect(new SpecialEffect { SpecialEffectTypeId = SpecialType.SkillUse, Value1 = (int)childId }));
            var result = SkillLaborBatch.Run(player, parent, true, () =>
            {
                Assert.Equal(1, player.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, material.TemplateId, 1, material));
                new SkillUse().Execute(player, new SkillCasterUnit(player.ObjId), player,
                    new SkillCastUnitTarget(player.ObjId), null, parent, null, DateTime.UtcNow, (int)childId, 0, 0, 0);
                Assert.Null(player.Buffs.GetEffectFromBuffId(buff.Id));
            });
            var succeeded = outcome == "success";
            Assert.Equal(succeeded, result);
            Assert.Equal(outcome == "labor" ? 0 : 1, checkpointCalls);
            Assert.Equal(succeeded ? 18 : labor, player.LaborPower);
            Assert.Equal(succeeded ? 18 : labor, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(succeeded ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
            Assert.Equal(succeeded ? 1 : 0, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={buff.Id}"));
            Assert.Equal(succeeded, player.Buffs.CheckBuff(buff.Id));
            var reloaded = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId, Name = "ReloadedChild" };
            ((Buffs)reloaded.Buffs).LoadActiveBuffs(reloaded);
            Assert.Equal(succeeded, reloaded.Buffs.CheckBuff(buff.Id));
        }
        finally
        {
            AppConfiguration.Instance.World = oldWorld;
            SwapSingleton(oldSkillRequirements);
            SwapSingleton(oldUnitRequirements);
            SwapSingleton(oldZones);
        }
    }

    [Fact]
    public void SkillLabor_TriggeredGraphRejectsDelayedBenefitsAndCycles()
    {
        using var services = new LaborBuffServices();
        var buff = services.AddTemplate(3645);
        var child = new SkillTemplate { Id = 17686 };
        child.Effects.Add(TriggeredEffect(new BuffEffect { Buff = buff }));
        services.AddSkill(child);
        var reference = new SpecialEffect { SpecialEffectTypeId = SpecialType.SkillUse, Value1 = (int)child.Id };
        Assert.True(Skill.CanSettleTriggeredEffect(reference));
        reference.Value2 = 1000;
        Assert.False(Skill.CanSettleTriggeredEffect(reference));
        child.Effects.Clear();
        child.Effects.Add(TriggeredEffect(new BubbleEffect()));
        Assert.True(Skill.CanSettleTriggeredEffect(reference));
        child.Effects.Clear();
        child.Effects.Add(TriggeredEffect(new SpecialEffect { SpecialEffectTypeId = SpecialType.SkillUse, Value1 = (int)child.Id }));
        reference.Value2 = 0;
        Assert.False(Skill.CanSettleTriggeredEffect(reference));
        child.Effects.Clear();
        child.Effects.Add(TriggeredEffect(new DamageEffect()));
        Assert.False(Skill.CanSettleTriggeredEffect(reference));
    }

    private static SkillEffect TriggeredEffect(EffectTemplate template) => new()
    {
        Template = template, Chance = 100, StartLevel = 1, EndLevel = 55,
        ApplicationMethod = SkillEffectApplicationMethod.Target
    };
}
