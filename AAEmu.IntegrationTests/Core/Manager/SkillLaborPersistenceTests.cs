using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SkillLabor_CheckpointsProductsMaterialsAndPersistentHarvestTogether(bool deleteSource, bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        var oldAccounts = SwapSingleton(new AccountManager(null, null, TimeProvider.System));
        var formulas = new FormulaManager();
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>());
        var oldFormulas = SwapSingleton(formulas);
        var experience = new ExperienceManager();
        var levels = new Mock<IExperienceLevelTemplateLoader>();
        levels.Setup(loader => loader.Load()).Returns(new[] {
            new ExperienceLevelTemplate { Level = 1, TotalExp = 0 },
            new ExperienceLevelTemplate { Level = 2, TotalExp = 1000000 } });
        experience.Load(levels.Object, 2, 2);
        var oldExperience = SwapSingleton(experience);
        var oldWorld = AAEmu.Game.Models.AppConfiguration.Instance.World;
        AAEmu.Game.Models.AppConfiguration.Instance.World = new AAEmu.Game.Models.Game.WorldConfig { ExpRate = 1 };
        try
        {
            var player = graph.Sender;
            player.Level = 1;
            player.Abilities = new AAEmu.Game.Models.Game.Char.CharacterAbilities(player);
            player.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Fight;
            player.Ability2 = player.Ability3 = AAEmu.Game.Models.Game.Skills.AbilityType.None;
            player.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            var material = graph.AddItem(0);
            material.Count = 1;
            var associatedItem = graph.AddItem(1);
            var system = graph.Items.GetItemContainerForCharacter(player.Id, SlotType.System, player, 0);
            Assert.True(system.AddOrMoveExistingItem(ItemTaskType.Invalid, associatedItem));
            var outputTemplate = player.Id + 4;
            var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
                .GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
            templates[outputTemplate] = new ItemTemplate { Id = outputTemplate, MaxCount = 100, FixedGrade = 0 };
            var allocator = (IItemIdManager)typeof(ItemManager)
                .GetField("<itemIdManager>P", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
            Mock.Get(allocator).Setup(manager => manager.GetNextId()).Returns(player.Id + 90);
            Assert.True(graph.Save.TryCommitEconomy([player]));
            var date = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
            var doodad = new Doodad { DbId = player.Id + 5, IsPersistent = true, TemplateId = 1,
                PlantTime = date, PhaseTime = date, GrowthTime = date, OwnerId = player.Id, ItemId = associatedItem.Id };
            doodad.Save();
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
            if (failBeforeCommit)
                skill.CommitLaborBatch = (character, write) => graph.Save.TryCommitEconomy([character], context =>
                {
                    write(context);
                    throw new InvalidOperationException("Forced failure after harvest and labor writes");
                });
            var deletedAfterCommit = false;
            var experienceBefore = player.Experience;
            var vocationBefore = player.VocationPoint;
            var result = SkillLaborBatch.Run(player, skill, true, () =>
            {
                player.AddExp(7, true);
                player.ChangeGamePoints(GamePointKind.Vocation, 9);
                SkillLaborBatch.Current.TrackDoodad(doodad);
                doodad.GrowthTime = date.AddHours(1);
                doodad.Save();
                if (deleteSource)
                    SkillLaborBatch.Current.DeleteDoodad(doodad, () => deletedAfterCommit = true);
                Assert.Equal(1, player.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, material.TemplateId, 1, material));
                Assert.True(player.Inventory.Bag.AcquireDefaultItem(ItemTaskType.SkillEffectGainItem, outputTemplate, 2));
            });
            Assert.Equal(!failBeforeCommit, result);
            Assert.Equal(failBeforeCommit ? experienceBefore : experienceBefore + 7, player.Experience);
            Assert.Equal(failBeforeCommit ? 0 : 7, player.Abilities.Abilities[player.Ability1].Exp);
            Assert.Equal(failBeforeCommit ? 0 : 7,
                Scalar($"SELECT exp FROM abilities WHERE owner={player.Id} AND id={(byte)player.Ability1}"));
            Assert.Equal(failBeforeCommit ? experienceBefore : experienceBefore + 7, Scalar($"SELECT experience FROM characters WHERE id={player.Id}"));
            Assert.Equal(failBeforeCommit ? vocationBefore : vocationBefore + 9, player.VocationPoint);
            Assert.Equal(failBeforeCommit ? vocationBefore : vocationBefore + 9, Scalar($"SELECT vocation_point FROM characters WHERE id={player.Id}"));
            Assert.Equal(failBeforeCommit ? 20 : 10, player.LaborPower);
            Assert.Equal(failBeforeCommit ? 20 : 10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
            Assert.Equal(failBeforeCommit ? 0 : 2, Scalar($"SELECT COALESCE(SUM(count),0) FROM items WHERE owner={player.Id} AND template_id={outputTemplate}"));
            Assert.Equal(deleteSource && !failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={doodad.DbId}"));
            Assert.Equal(deleteSource && !failBeforeCommit, deletedAfterCommit);
            Assert.Equal(deleteSource && !failBeforeCommit ? 0 : 1,
                Scalar($"SELECT COUNT(*) FROM items WHERE id={associatedItem.Id}"));
            if (!deleteSource || failBeforeCommit)
                Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT HOUR(growth_time) FROM doodads WHERE id={doodad.DbId}"));
            Assert.Equal(failBeforeCommit ? date : date.AddHours(1), doodad.GrowthTime);
            var reloaded = graph.ReloadLifecycle();
            Assert.Equal(failBeforeCommit, reloaded.Items.GetItemByItemId(material.Id) != null);
            Assert.Equal(!failBeforeCommit, reloaded.Items.GetItemByItemId(player.Id + 90) != null);
        }
        finally
        {
            SwapSingleton(oldAccounts);
            SwapSingleton(oldFormulas);
            SwapSingleton(oldExperience);
            AAEmu.Game.Models.AppConfiguration.Instance.World = oldWorld;
        }
    }
    [Fact]
    public void SkillLabor_NestedCheckpointRejectsAndRestoresTheWholeBatch()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var player = graph.Sender;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
        var material = graph.AddItem(0);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
        var count = material.Count;
        Assert.False(SkillLaborBatch.Run(player, skill, true, () =>
        {
            Assert.Equal(1, player.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, material.TemplateId, 1, material));
            Assert.False(graph.Save.TryCommitEconomy([player]));
        }));
        Assert.True(skill.Cancelled);
        Assert.Equal(20, player.LaborPower);
        Assert.Equal(count, material.Count);
        Assert.Equal(count, Scalar($"SELECT count FROM items WHERE id={material.Id}"));
        Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
    }

}
