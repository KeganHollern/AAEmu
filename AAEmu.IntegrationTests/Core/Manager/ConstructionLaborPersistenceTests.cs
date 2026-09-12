using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("no_labor")]
    [InlineData("failed_commit")]
    public void HouseConstruction_PersistsProgressWithLaborAndMaterial(string outcome)
    {
        using var graph = new SendGraph();
        var oldAccounts = SwapSingleton(new AccountManager(null, null, TimeProvider.System));
        var formulas = new FormulaManager();
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>());
        var oldFormulas = SwapSingleton(formulas);
        try
        {
            var player = graph.Sender;
            var initialLabor = outcome == "no_labor" ? (short)0 : (short)20;
            player.InitializeLaborCache(initialLabor, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},{initialLabor}) ON DUPLICATE KEY UPDATE labor={initialLabor}");
            var material = graph.AddItem(0);
            material.Count = 1;
            var template = new HousingTemplate { MainModelId = 90, HousingBindingDoodad = [] };
            template.BuildSteps.Add(0, new HousingBuildStep { NumActions = 2, ModelId = 10, SkillId = 50 });
            var house = new House
            {
                Id = player.Id, AccountId = player.AccountId, OwnerId = player.Id,
                TemplateId = 1, Template = template, CurrentStep = 0, NumAction = 1,
                Name = "Construction checkpoint", Faction = new SystemFaction(),
                PlaceDate = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
                ProtectionEndDate = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
            };
            Assert.True(graph.Save.TryCommitEconomy([player], context => house.Save(context)));
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
            if (outcome == "failed_commit")
                skill.CommitLaborBatch = (character, write) => graph.Save.TryCommitEconomy([character], context =>
                {
                    write(context);
                    throw new InvalidOperationException("Injected failure after house and labor writes");
                });

            var result = SkillLaborBatch.Run(player, skill, true, () =>
            {
                Assert.Equal(1, player.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents,
                    material.TemplateId, 1, material));
                CraftEffect.AdvanceHouseConstruction(player, house, 50, skill);
            });

            var success = outcome == "success";
            Assert.Equal(success, result);
            Assert.Equal(success ? 10 : initialLabor, player.LaborPower);
            Assert.Equal(success ? 10 : initialLabor, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(success ? -1 : 0, house.CurrentStep);
            Assert.Equal(success ? 0 : 1, house.NumAction);
            Assert.Equal(success ? 90U : 10U, house.ModelId);
            Assert.False(house.IsDirty);
            Assert.Equal(success ? -1 : 0, Scalar($"SELECT current_step FROM housings WHERE id={house.Id}"));
            Assert.Equal(success ? 0 : 1, Scalar($"SELECT current_action FROM housings WHERE id={house.Id}"));
            Assert.Equal(success ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
            Assert.Equal(success ? 0 : 1, player.Inventory.Bag.Items.Count(item => item.Id == material.Id));
        }
        finally
        {
            SwapSingleton(oldAccounts);
            SwapSingleton(oldFormulas);
        }
    }
}
