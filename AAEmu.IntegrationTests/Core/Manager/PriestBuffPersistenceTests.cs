using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(1800000)]
    [InlineData(30000)]
    public void PriestPurchase_BuffSurvivesSecondRestartBeforeAutosave(int timeLeft)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        using var dataConnection = new SqliteConnection("Data Source=:memory:");
        dataConnection.Open();
        using (var command = dataConnection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE priest_buffs(id INTEGER,buff_id INTEGER,cost INTEGER); INSERT INTO priest_buffs VALUES(1,239,100)";
            command.ExecuteNonQuery();
        }
        var data = new PriestBuffGameData();
        data.Load(dataConnection);
        var oldPriest = SwapSingleton(data);
        try
        {
            var player = graph.Sender;
            var template = new BuffTemplate { Id = 239, Kind = BuffKind.Good,
                SaveRuleId = BuffSaveRuleType.Normal, Duration = 1800000, StackRule = BuffStackRule.Refresh };
            buffs.AddTemplate(template);
            Assert.False(SkillManager.Instance.IsPaidSkillBuff(template.Id));
            Assert.True(data.Get(1).TryGetCost(player.Level, out var cost));
            Assert.Equal(5000, cost);
            var startMoney = player.Money;
            var buff = NewLaborBuff(player, template, 1);
            Assert.True(player.CompletePriestPurchase(cost,
                () => player.Buffs.AddBuff(buff, forcedDuration: timeLeft),
                () => player.Buffs.RemoveEffect(buff),
                () => graph.Save.TryCommitEconomy([player])));
            Character restored = null;
            for (var restart = 0; restart < 2; restart++)
            {
                restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
                ((Buffs)restored.Buffs).LoadActiveBuffs(restored);
                Assert.InRange(restored.Buffs.GetEffectFromBuffId(template.Id).GetTimeLeft(), timeLeft - 10000, timeLeft);
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={template.Id}"));
                Assert.Equal(startMoney - cost, Scalar($"SELECT money FROM characters WHERE id={player.Id}"));
            }
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            ((Buffs)restored.Buffs).SaveActiveBuffs(connection, transaction, player.Id);
            transaction.Commit();
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={template.Id}"));
        }
        finally { SwapSingleton(oldPriest); }
    }
}
