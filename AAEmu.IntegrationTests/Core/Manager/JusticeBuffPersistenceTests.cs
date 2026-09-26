using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(631u, 1800000, 1800000)]
    [InlineData(631u, 1800000, 15000)]
    [InlineData(2028u, 1800000, 15000)]
    [InlineData(2167u, 30000, 20000)]
    [InlineData(4424u, 180000, 15000)]
    [InlineData(4863u, 86400000, 15000)]
    [InlineData(4868u, 36000000, 15000)]
    public void JusticePenalty_PausesOfflineAndSurvivesRepeatedRestore(uint id, int duration, int timeLeft)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var template = new BuffTemplate { Id = id, Kind = BuffKind.Bad,
            SaveRuleId = BuffSaveRuleType.Normal, Duration = duration, StackRule = BuffStackRule.Refresh };
        services.AddTemplate(template);
        var player = graph.Sender;
        player.Buffs.AddBuff(NewLaborBuff(player, template, 1), forcedDuration: timeLeft);
        SaveJusticeBuffs(player);
        Execute($"UPDATE character_active_buffs SET saved_at=DATE_SUB(UTC_TIMESTAMP(), INTERVAL 40 DAY) WHERE character_id={player.Id}");
        for (var restart = 0; restart < 2; restart++)
        {
            var restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
            restored.Buffs.LoadActiveBuffs(restored);
            var penalty = restored.Buffs.GetEffectFromBuffId(id);
            Assert.NotNull(penalty);
            Assert.InRange(penalty.GetTimeLeft(), timeLeft - 10000, timeLeft);
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={id}"));
            if (restart == 1)
                SaveJusticeBuffs(restored);
        }
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={id}"));
    }

    [Fact]
    public void Wanted_PermanentMarkerSurvivesRepeatedRestore()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var template = new BuffTemplate { Id = (uint)BuffConstants.Wanted, Kind = BuffKind.Bad,
            SaveRuleId = BuffSaveRuleType.Normal, Duration = 0, StackRule = BuffStackRule.Refresh };
        services.AddTemplate(template);
        var player = graph.Sender;
        player.Buffs.AddBuff(NewLaborBuff(player, template, 1));
        SaveJusticeBuffs(player);
        for (var restart = 0; restart < 2; restart++)
        {
            var restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
            restored.Buffs.LoadActiveBuffs(restored);
            Assert.True(restored.Buffs.CheckBuff(template.Id));
            Assert.Equal(0, restored.Buffs.GetEffectFromBuffId(template.Id).Duration);
            if (restart == 1)
                SaveJusticeBuffs(restored);
        }
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={template.Id}"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public void RealTimePenalty_ExpiresOfflineWithoutIntegerOverflow(int days)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var template = new BuffTemplate { Id = 990001, Kind = BuffKind.Bad, RealTime = true,
            SaveRuleId = BuffSaveRuleType.Normal, Duration = 30000, StackRule = BuffStackRule.Refresh };
        services.AddTemplate(template);
        var player = graph.Sender;
        player.Buffs.AddBuff(NewLaborBuff(player, template, 1));
        SaveJusticeBuffs(player);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id} AND buff_id={template.Id}"));
        Execute($"UPDATE character_active_buffs SET saved_at=DATE_SUB(UTC_TIMESTAMP(), INTERVAL {days} DAY) WHERE character_id={player.Id}");
        var restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
        restored.Buffs.LoadActiveBuffs(restored);
        Assert.False(restored.Buffs.CheckBuff(template.Id));
        SaveJusticeBuffs(restored);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM character_active_buffs WHERE character_id={player.Id}"));
    }

    private static void SaveJusticeBuffs(Character player)
    {
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        ((Buffs)player.Buffs).SaveActiveBuffs(connection, transaction, player.Id);
        transaction.Commit();
    }

    [Fact]
    public void ForgivenessQuest_KeepsWantedAtZeroCrimeAndAfterRelog()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        services.AddTemplate(new BuffTemplate { Id = (uint)BuffConstants.Wanted, Kind = BuffKind.Bad,
            SaveRuleId = BuffSaveRuleType.Normal, Duration = 0, StackRule = BuffStackRule.Refresh });
        var player = graph.Sender;
        player.AddCrime(50);
        Assert.True(player.Buffs.CheckBuff((uint)BuffConstants.Wanted));
        var act = new QuestActSupplyCrimePoint(null) { Point = -100 };
        var quest = new Quest(null, player, null, null, null, null, null, initializeQuestActs: false);
        Assert.True(act.RunAct(quest, null, 0));
        Assert.Equal(0, player.CrimePoint);
        Assert.Equal(0, player.InfamyPoint);
        Assert.True(player.Buffs.CheckBuff((uint)BuffConstants.Wanted));
        SaveJusticeBuffs(player);
        var restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
        restored.Buffs.LoadActiveBuffs(restored);
        restored.CheckWantedThreshold();
        Assert.True(restored.Buffs.CheckBuff((uint)BuffConstants.Wanted));
    }

    [Theory]
    [InlineData(0, 10000)]
    [InlineData(4, 240000)]
    public void RearrestSentence_AddsOldRemainderAfterNewVerdictAndSurvivesRelog(int newMinutes, int newMilliseconds)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        foreach (var id in new[] { (uint)BuffConstants.Prisoner_Nuian, (uint)BuffConstants.Prisoner_Haranyan })
            services.AddTemplate(new BuffTemplate { Id = id, Kind = BuffKind.Bad, SaveRuleId = BuffSaveRuleType.Normal,
                Duration = 1800000, StackRule = BuffStackRule.Refresh });
        var player = graph.Sender;
        player.Buffs.AddBuff((uint)BuffConstants.Prisoner_Nuian, player, 90000);
        player.SetPendingTrialSentence(newMinutes, CourtRoomRegion.Haranyan);
        Assert.True(player.HasPendingTrial);
        Assert.True(player.ApplyPrisonSentence(CourtRoomRegion.Haranyan, newMinutes));
        Assert.False(player.HasPendingTrial);
        Assert.False(player.Buffs.CheckBuff((uint)BuffConstants.Prisoner_Nuian));
        Assert.InRange(player.GetUnservedPrisonMilliseconds(), 80000 + newMilliseconds, 90000 + newMilliseconds);
        SaveJusticeBuffs(player);
        var restored = new Character(new UnitCustomModelParams()) { Id = player.Id, ObjId = player.ObjId };
        restored.Buffs.LoadActiveBuffs(restored);
        Assert.InRange(restored.GetUnservedPrisonMilliseconds(), 80000 + newMilliseconds, 90000 + newMilliseconds);
    }

    [Theory]
    [InlineData(20, 200)]
    [InlineData(50, 20)]
    public void Sentence_UsesTheSameVictimLevelOnlineAndOffline(byte level, int minutes)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        graph.Receiver.Level = level;
        Execute($"UPDATE characters SET level={level} WHERE id={graph.Receiver.Id}");
        var trial = new TrialData { Defendant = graph.Sender, CourtRegion = CourtRoomRegion.Nuian,
            EvidenceList = [new CrimeEvent { Victim = graph.Receiver.Id, CrimeKind = CrimeKind.Murder }] };
        trial.CalculateJailTime();
        Assert.Equal(minutes, trial.JailTime);
        Assert.True(WorldManager.Instance.TryAddCharacter(graph.Receiver));
        trial.CalculateJailTime();
        Assert.Equal(minutes, trial.JailTime);
    }
}
