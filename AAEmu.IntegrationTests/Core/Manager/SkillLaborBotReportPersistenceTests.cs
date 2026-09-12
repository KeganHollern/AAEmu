using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Units;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillLabor_BotReportSavesCountersBuffAndLaborTogether(bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var oldSus = SwapSingleton(new SusManager(null));
        try
        {
            var player = graph.Sender;
            var bot = graph.Receiver;
            player.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            var template = AddBotReportBuff(buffs, (uint)BuffConstants.SuspectedUser);
            AddBotReportBuff(buffs, (uint)BuffConstants.TransformingIntoPrimeSuspect);
            var crime = new CrimeManager();
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
            if (failBeforeCommit)
                skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
                {
                    write(context);
                    throw new InvalidOperationException("Forced bot report checkpoint failure");
                });
            Assert.Equal(!failBeforeCommit, SkillLaborBatch.Run(player, skill, true, () =>
            {
                Assert.True(crime.ReportBot(bot, player, "Report test"));
                Assert.False(bot.Buffs.CheckBuff(template.Id));
            }));
            Assert.Equal(failBeforeCommit ? 20 : 10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT bot_reported_count FROM characters WHERE id={player.Id}"));
            Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT reported_as_bot_count FROM characters WHERE id={bot.Id}"));
            Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM bot_reports WHERE reported_id={bot.Id} AND reporter_id={player.Id}"));
            Assert.Equal(failBeforeCommit ? 0 : 1, player.BotReportedCount);
            Assert.Equal(failBeforeCommit ? 0 : 1, bot.ReportedAsBotCount);
            Assert.Equal(!failBeforeCommit, bot.Buffs.CheckBuff(template.Id));
            var restored = new Character(new UnitCustomModelParams()) { Id = bot.Id, ObjId = bot.ObjId };
            ((Buffs)restored.Buffs).LoadActiveBuffs(restored);
            Assert.Equal(!failBeforeCommit, restored.Buffs.CheckBuff(template.Id));
            if (failBeforeCommit)
            {
                player.SkillCancelled = false;
                var retry = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
                Assert.True(SkillLaborBatch.Run(player, retry, true, () => Assert.True(crime.ReportBot(bot, player, "Retry"))));
            }
            else
            {
                Assert.True(graph.Save.TryCommitEconomy([player, bot]));
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM bot_reports WHERE reported_id={bot.Id} AND reporter_id={player.Id}"));
                Assert.Equal(1, Scalar($"SELECT reported_as_bot_count FROM characters WHERE id={bot.Id}"));
                Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={bot.Id} AND buff_id={template.Id}"));
                // A second restart before autosave must keep the paid permanent report marker.
                var secondRestart = new Character(new UnitCustomModelParams()) { Id = bot.Id, ObjId = bot.ObjId };
                ((Buffs)secondRestart.Buffs).LoadActiveBuffs(secondRestart);
                Assert.True(secondRestart.Buffs.CheckBuff(template.Id));
                crime = new CrimeManager();
                crime.LoadBotReports();
                var duplicate = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
                Assert.False(SkillLaborBatch.Run(player, duplicate, true, () =>
                {
                    Assert.False(crime.ReportBot(bot, player, "Duplicate"));
                    SkillLaborBatch.Current.Fail();
                }));
                Assert.Equal(10, player.LaborPower);
            }
        }
        finally { SwapSingleton(oldSus); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillLabor_BotAppealRemovesPersistedBuffWithoutTransientReportList(bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var oldSus = SwapSingleton(new SusManager(null));
        try
        {
            var player = graph.Sender;
            player.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            var template = AddBotReportBuff(buffs, (uint)BuffConstants.TransformingIntoPrimeSuspect);
            SetField(SkillManager.Instance, "_taggedBuffs", new Dictionary<uint, List<uint>>
                { [(uint)BuffConstants.TagSuspects] = [template.Id] });
            player.Buffs.AddBuff(NewLaborBuff(player, template, 1));
            Assert.True(graph.Save.TryCommitEconomy([player]));
            Execute($"INSERT INTO bot_reports(reported_id,reporter_id) VALUES({player.Id},{graph.Receiver.Id})");
            // A restarted CrimeManager has no in-memory report list, but the paid buff persists.
            var crime = new CrimeManager();
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
            if (failBeforeCommit)
                skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
                {
                    write(context);
                    throw new InvalidOperationException("Forced bot appeal checkpoint failure");
                });
            Assert.Equal(!failBeforeCommit, SkillLaborBatch.Run(player, skill, true, () => Assert.True(crime.ReportBotExpired(player))));
            Assert.Equal(failBeforeCommit ? 20 : 10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id}"));
            Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM bot_reports WHERE reported_id={player.Id}"));
            Assert.Equal(failBeforeCommit, player.Buffs.CheckBuff(template.Id));
            var restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
            ((Buffs)restored.Buffs).LoadActiveBuffs(restored);
            Assert.Equal(failBeforeCommit, restored.Buffs.CheckBuff(template.Id));
        }
        finally { SwapSingleton(oldSus); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillLabor_BotFreeAppealUsesTheSameDurableRemoval(bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var oldSus = SwapSingleton(new SusManager(null));
        var oldCrime = SwapSingleton(new CrimeManager());
        try
        {
            var player = graph.Sender;
            player.InitializeLaborCache(0, DateTime.UtcNow);
            var template = AddBotReportBuff(buffs, (uint)BuffConstants.TransformingIntoPrimeSuspect);
            SetField(SkillManager.Instance, "_taggedBuffs", new Dictionary<uint, List<uint>>
                { [(uint)BuffConstants.TagSuspects] = [template.Id] });
            player.Buffs.AddBuff(NewLaborBuff(player, template, 1));
            Assert.True(graph.Save.TryCommitEconomy([player]));
            Execute($"INSERT INTO bot_reports(reported_id,reporter_id) VALUES({player.Id},{graph.Receiver.Id})");
            var skill = new Skill(new SkillTemplate { Id = 21416, ConsumeLaborPower = 0 });
            if (failBeforeCommit)
                skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
                {
                    write(context);
                    throw new InvalidOperationException("Forced free appeal checkpoint failure");
                });
            new AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects.ReportBotExpired().Execute(player,
                new SkillCasterUnit(player.ObjId), player, new SkillCastUnitTarget(player.ObjId),
                new CastSkill(skill.Id, 0), skill, null, DateTime.UtcNow, 0, 0, 0, 0);
            Assert.Equal(failBeforeCommit, skill.Cancelled);
            Assert.Equal(0, player.LaborPower);
            Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM bot_reports WHERE reported_id={player.Id}"));
            Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id}"));
        }
        finally
        {
            SwapSingleton(oldCrime);
            SwapSingleton(oldSus);
        }
    }

    [Fact]
    public void SkillLabor_BotReportUpdateCanRepeatWithoutLosingReports()
    {
        using var graph = new SendGraph();
        Execute($"INSERT INTO bot_reports(reported_id,reporter_id) VALUES({graph.Sender.Id},{graph.Receiver.Id})");
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SQL", "updates",
            "2026-09-12_aaemu_game_bot_reports.sql"));
        Execute(sql);
        Execute(sql);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM bot_reports WHERE reported_id={graph.Sender.Id} AND reporter_id={graph.Receiver.Id}"));
    }

    [Theory]
    [InlineData((uint)BuffConstants.TransformingIntoPrimeSuspect)]
    [InlineData((uint)BuffConstants.PrimeSuspect)]
    public void SkillLabor_BotStatusKeepsItsLastMinuteAcrossRestartAndLaterSave(uint buffId)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var player = graph.Sender;
        var template = AddBotReportBuff(buffs, buffId);
        // A status restored near expiry still needs persistence with less than one minute left.
        player.Buffs.AddBuff(NewLaborBuff(player, template, 1), forcedDuration: 30000);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        for (var restart = 0; restart < 2; restart++)
        {
            var restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
            ((Buffs)restored.Buffs).LoadActiveBuffs(restored);
            Assert.InRange(restored.Buffs.GetEffectFromBuffId(buffId).GetTimeLeft(), 20000, 30000);
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            ((Buffs)restored.Buffs).SaveActiveBuffs(connection, transaction, player.Id);
            transaction.Commit();
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={buffId}"));
        }
    }

    private static BuffTemplate AddBotReportBuff(LaborBuffServices buffs, uint id)
    {
        var template = new BuffTemplate { Id = id,
            Duration = id == (uint)BuffConstants.SuspectedUser ? 0 : 60000,
            Kind = id == (uint)BuffConstants.SuspectedUser ? BuffKind.Good : BuffKind.Bad,
            SaveRuleId = BuffSaveRuleType.Normal, StackRule = BuffStackRule.Refresh, MaxStack = 1,
            InitMinCharge = 1, InitMaxCharge = 2 };
        buffs.AddTemplate(template);
        return template;
    }

}
