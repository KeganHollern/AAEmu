using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
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
    public void SkillLabor_PersistentBuffAndLaborCommitTogether(bool refresh, bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var player = graph.Sender;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
        var template = buffs.AddTemplate(player.Id + 10);
        Buff original = null;
        if (refresh)
        {
            original = NewLaborBuff(player, template, 1);
            player.Buffs.AddBuff(original, forcedDuration: 120000);
        }
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var start = original?.StartTime;
        var duration = original?.Duration;
        var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
        if (failBeforeCommit)
            skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
            {
                write(context);
                throw new InvalidOperationException("Forced failure after paid buff write");
            });
        var added = NewLaborBuff(player, template, 7);
        var started = 0;
        added.Events.OnBuffStarted += (_, _) => started++;
        var result = SkillLaborBatch.Run(player, skill, true, () =>
        {
            player.Buffs.AddBuff(added);
            Assert.Same(original, player.Buffs.GetEffectFromBuffId(template.Id));
            Assert.Equal(0, started);
        });
        Assert.Equal(!failBeforeCommit, result);
        Assert.Equal(failBeforeCommit ? 20 : 10, player.LaborPower);
        Assert.Equal(failBeforeCommit ? 20 : 10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Equal(refresh || !failBeforeCommit ? 1 : 0,
            Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={template.Id}"));
        if (failBeforeCommit)
        {
            Assert.Same(original, player.Buffs.GetEffectFromBuffId(template.Id));
            Assert.Equal(start, original?.StartTime);
            Assert.Equal(duration, original?.Duration);
            Assert.Equal(0, started);
        }
        else
        {
            Assert.Equal(7, Scalar($"SELECT charge FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={template.Id}"));
            Assert.Equal(600000, Scalar($"SELECT duration FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={template.Id}"));
            Assert.Equal(7, player.Buffs.GetEffectFromBuffId(template.Id).Charge);
            Assert.Equal(refresh ? 0 : 1, started);
        }
        // A fresh instance uses the same login loader as a process restart.
        var reloaded = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId, Name = "Reloaded" };
        ((Buffs)reloaded.Buffs).LoadActiveBuffs(reloaded);
        var restored = reloaded.Buffs.GetEffectFromBuffId(template.Id);
        if (refresh || !failBeforeCommit)
        {
            Assert.NotNull(restored);
            Assert.Equal(failBeforeCommit ? 1 : 7, restored.Charge);
            Assert.InRange(restored.GetTimeLeft(), failBeforeCommit ? 90000 : 570000, failBeforeCommit ? 120000 : 600000);
        }
        else Assert.Null(restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillLabor_BuffSqlFailureRestoresLaborAndLiveBuff(bool anotherCharacter)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var player = graph.Sender;
        var target = anotherCharacter ? graph.Receiver : player;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
        var template = buffs.AddTemplate(player.Id + 10);
        var trigger = $"reject_paid_buff_{player.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON character_active_buffs FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Forced paid buff SQL failure'");
        try
        {
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
            Assert.False(SkillLaborBatch.Run(player, skill, true, () => target.Buffs.AddBuff(NewLaborBuff(target, template, 7))));
            Assert.Equal(20, player.LaborPower);
            Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Null(target.Buffs.GetEffectFromBuffId(template.Id));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={target.Id}"));
        }
        finally { Execute($"DROP TRIGGER {trigger}"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillLabor_DispelAndLaborCommitTogether(bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var player = graph.Sender;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
        var template = buffs.AddTemplate(player.Id + 10);
        var original = NewLaborBuff(player, template, 3);
        player.Buffs.AddBuff(original);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
        if (failBeforeCommit)
            skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
            {
                write(context);
                throw new InvalidOperationException("Forced failure after paid dispel write");
            });
        Assert.Equal(!failBeforeCommit, SkillLaborBatch.Run(player, skill, true, () =>
        {
            player.Buffs.RemoveBuffs(BuffKind.Good, 1);
            Assert.Same(original, player.Buffs.GetEffectFromBuffId(template.Id));
        }));
        Assert.Equal(failBeforeCommit ? 20 : 10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id}"));
        Assert.Equal(failBeforeCommit, player.Buffs.CheckBuff(template.Id));
        var reloaded = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId, Name = "Reloaded" };
        ((Buffs)reloaded.Buffs).LoadActiveBuffs(reloaded);
        Assert.Equal(failBeforeCommit, reloaded.Buffs.CheckBuff(template.Id));
    }

    [Fact]
    public void SkillLabor_RestartRestoresPaidBuffWhenProcessStopsBeforeLiveCallback()
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        var player = graph.Sender;
        player.InitializeLaborCache(20, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
        var template = buffs.AddTemplate(player.Id + 10);
        var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
        var stopped = false;
        graph.Save.StopForConsistencyFailure = (_, _) => stopped = true;
        Assert.Throws<InvalidOperationException>(() => SkillLaborBatch.Run(player, skill, true, () =>
        {
            SkillLaborBatch.Current.AfterCommit(() => throw new InvalidOperationException("Simulated process stop after commit"));
            player.Buffs.AddBuff(NewLaborBuff(player, template, 7));
        }));
        Assert.Equal(10, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Null(player.Buffs.GetEffectFromBuffId(template.Id));
        Assert.True(stopped);
        Assert.Throws<InvalidOperationException>(() => graph.Save.TryCommitEconomy([player]));
        var reloaded = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId, Name = "Reloaded" };
        ((Buffs)reloaded.Buffs).LoadActiveBuffs(reloaded);
        Assert.Equal(7, reloaded.Buffs.GetEffectFromBuffId(template.Id).Charge);
    }

    private static Buff NewLaborBuff(Character owner, BuffTemplate template, int charge) =>
        new(owner, owner, new SkillCasterUnit(owner.ObjId), template, null, DateTime.UtcNow) { Charge = charge };

    private sealed class LaborBuffServices : IDisposable
    {
        private readonly List<Action> _restore = [];
        private readonly SkillManager _skills = new(null, null);
        public LaborBuffServices()
        {
            Replace(new AccountManager(null, null, TimeProvider.System));
            var formulas = new FormulaManager();
            SetField(formulas, "_formulas", new Dictionary<uint, Formula>());
            Replace(formulas);
            foreach (var field in typeof(SkillManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                field.SetValue(_skills, Activator.CreateInstance(field.FieldType));
            Replace(_skills);
            var buffData = new BuffGameData();
            foreach (var field in typeof(BuffGameData).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                field.SetValue(buffData, Activator.CreateInstance(field.FieldType));
            Replace(buffData);
            Replace(new EffectTaskManager(Mock.Of<ITaskManager>()));
            Replace(new TaskManager(Mock.Of<ITickManager>()));
        }
        public BuffTemplate AddTemplate(uint id)
        {
            var template = new BuffTemplate { Id = id, Duration = 600000, Kind = BuffKind.Good,
                SaveRuleId = BuffSaveRuleType.Normal, StackRule = BuffStackRule.Refresh, MaxStack = 1,
                InitMinCharge = 1, InitMaxCharge = 2 };
            ((Dictionary<uint, BuffTemplate>)typeof(SkillManager)
                .GetField("_buffs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_skills)!)[id] = template;
            return template;
        }
        private void Replace<T>(T manager) where T : class
        {
            var previous = SwapSingleton(manager);
            _restore.Add(() => SwapSingleton(previous));
        }
        public void Dispose()
        {
            foreach (var restore in _restore.AsEnumerable().Reverse()) restore();
        }
    }
}
