using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillLabor_TemperingSavesEquipmentLaborAndSourceTogether(bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        var oldAccounts = SwapSingleton(new AccountManager(null, null, TimeProvider.System));
        var formulas = new FormulaManager();
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>());
        var oldFormulas = SwapSingleton(formulas);
        try
        {
            var player = graph.Sender;
            player.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            var source = graph.AddItem(0);
            var equipment = graph.AddEquipment(1);
            SetField(graph.Items, "_itemCapScales", new Dictionary<uint, ItemCapScale>
            { [50] = new() { SkillId = 50, ScaleMin = 105, ScaleMax = 106 } });
            Assert.True(graph.Save.TryCommitEconomy([player]));
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
            if (failBeforeCommit)
                skill.CommitLaborBatch = (owner, write) => graph.Save.TryCommitEconomy([owner], context =>
                {
                    write(context);
                    throw new InvalidOperationException("Forced tempering checkpoint failure");
                });
            Assert.Equal(!failBeforeCommit, SkillLaborBatch.Run(player, skill, true, () =>
            {
                new AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects.ItemCapScale().Execute(player,
                    new SkillItem(player.ObjId, source.Id, source.TemplateId), player,
                    new SkillCastItemTarget { Id = equipment.Id }, new CastSkill(50, 0), skill, null,
                    DateTime.UtcNow, 0, 0, 0, 0);
                Assert.Equal(1, player.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, source.TemplateId, 1, source));
            }));
            Assert.Equal(failBeforeCommit ? 20 : 10, player.LaborPower);
            Assert.Equal(failBeforeCommit ? 20 : 10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(failBeforeCommit ? 5 : 4, Scalar($"SELECT count FROM items WHERE id={source.Id}"));
            Assert.Equal(failBeforeCommit ? 104 : 105, equipment.TemperPhysical);
            Assert.Equal(failBeforeCommit ? 103 : 105, equipment.TemperMagical);
            var loaded = Assert.IsType<EquipItem>(graph.ReloadLifecycle().Items.GetItemByItemId(equipment.Id));
            Assert.Equal(failBeforeCommit ? 104 : 105, loaded.TemperPhysical);
            Assert.Equal(failBeforeCommit ? 103 : 105, loaded.TemperMagical);
        }
        finally
        {
            SwapSingleton(oldAccounts);
            SwapSingleton(oldFormulas);
        }
    }
}
