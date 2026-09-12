using System.Buffers.Binary;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ExperienceRecovery_PaymentSourceAndRawExperienceCommitTogether(bool paidLabor, bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        // A nonempty labor formula, world rate, and both XP buffs expose unwanted gain rewards.
        using var services = new LaborRewardServices(graph.Sender, false);
        var player = graph.Sender;
        var laborBefore = paidLabor ? 10 : 0;
        var laborAfter = paidLabor ? 9 : 0;
        player.InitializeLaborCache((short)laborBefore, DateTime.UtcNow);
        player.ConsumedLaborPower = 11;
        player.RecoverableExp = 400;
        player.LastExpLoss = 500;
        var scroll = graph.AddItem(0);
        scroll.Count = 1;
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},{laborBefore}) ON DUPLICATE KEY UPDATE labor={laborBefore}");
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var notifications = new List<ushort>();
        var session = new Mock<ISession>();
        player.Connection = new GameConnection(session.Object) { ActiveChar = player };
        session.Setup(value => value.SendPacket(It.IsAny<byte[]>())).Callback<byte[]>(bytes =>
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
            notifications.Add(type);
            if (type is SCOffsets.SCExpChangedPacket or SCOffsets.SCRecoverableExpPacket or SCOffsets.SCCharacterLaborPowerChangedPacket)
            {
                Assert.False(failBeforeCommit);
                Assert.Equal(420, Scalar($"SELECT experience FROM characters WHERE id={player.Id}"));
                Assert.Equal(0, Scalar($"SELECT recoverable_exp FROM characters WHERE id={player.Id}"));
                Assert.Equal(laborAfter, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            }
        });
        var skill = new Skill(new SkillTemplate { Id = paidLabor ? 17063U : 26611U });
        var effect = new RecoverExpEffect { NeedLaborPower = paidLabor };
        skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy([player], context =>
        {
            Assert.Empty(notifications);
            Assert.Equal(420, player.Experience);
            Assert.Equal(30, player.Abilities.Abilities[AbilityType.Fight].Exp);
            Assert.Equal(7, player.Actability.Actabilities[(uint)ActabilityType.Commerce].Point);
            write(context);
            if (failBeforeCommit)
                throw new InvalidOperationException("Failure after recovery, source, and labor writes");
        });

        Assert.Equal(!failBeforeCommit, SkillLaborBatch.Run(player, skill, true, () =>
        {
            effect.Apply(player, null, player, null, null, new EffectSource(skill), null, DateTime.UtcNow);
            if (!paidLabor && !skill.Cancelled)
                Assert.True(SkillLaborBatch.Current.Inventory.TryConsume(player.Inventory.Bag, scroll, 1));
        }));

        Assert.Equal(failBeforeCommit ? 20 : 420, player.Experience);
        Assert.Equal(failBeforeCommit ? 400 : 0, player.RecoverableExp);
        Assert.Equal(failBeforeCommit ? 500 : 0, player.LastExpLoss);
        Assert.Equal(failBeforeCommit ? laborBefore : laborAfter, player.LaborPower);
        Assert.Equal(11 + (!failBeforeCommit && paidLabor ? 1 : 0), player.ConsumedLaborPower);
        Assert.Equal(30, player.Abilities.Abilities[AbilityType.Fight].Exp);
        Assert.Equal(7, player.Actability.Actabilities[(uint)ActabilityType.Commerce].Point);
        Assert.Equal(paidLabor || failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM items WHERE id={scroll.Id}"));
        Assert.Equal(failBeforeCommit ? 0 : 1, notifications.Count(type => type == SCOffsets.SCExpChangedPacket));
        Assert.Equal(failBeforeCommit ? 0 : 1, notifications.Count(type => type == SCOffsets.SCRecoverableExpPacket));
        Assert.Equal(!failBeforeCommit && paidLabor ? 1 : 0,
            notifications.Count(type => type == SCOffsets.SCCharacterLaborPowerChangedPacket));

        // A fresh connection and character discard the prepared in-memory recovery state.
        using var connection = MySQL.CreateConnection();
        var reloaded = new Character(new UnitCustomModelParams()) { Id = player.Id, AccountId = player.AccountId };
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT c.level,c.experience,c.recoverable_exp,c.consumed_lp,a.labor FROM characters c JOIN accounts a ON a.account_id=c.account_id WHERE c.id={player.Id}";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            reloaded.Level = reader.GetByte("level");
            typeof(Character).GetProperty(nameof(Character.Experience))!.SetValue(reloaded, reader.GetInt32("experience"));
            reloaded.RecoverableExp = reader.GetInt32("recoverable_exp");
            reloaded.ConsumedLaborPower = reader.GetInt32("consumed_lp");
            reloaded.InitializeLaborCache(reader.GetInt16("labor"), DateTime.UtcNow);
        }
        reloaded.Abilities = new CharacterAbilities(reloaded);
        reloaded.Abilities.Load(connection);
        Assert.Equal(player.Experience, reloaded.Experience);
        Assert.Equal(player.RecoverableExp, reloaded.RecoverableExp);
        Assert.Equal(player.LaborPower, reloaded.LaborPower);
        Assert.Equal(player.ConsumedLaborPower, reloaded.ConsumedLaborPower);
        Assert.Equal(30, reloaded.Abilities.Abilities[AbilityType.Fight].Exp);
        if (!failBeforeCommit)
        {
            var retry = new Skill(new SkillTemplate { Id = skill.Template.Id });
            Assert.False(SkillLaborBatch.Run(reloaded, retry, true, () =>
                effect.Apply(reloaded, null, reloaded, null, null, new EffectSource(retry), null, DateTime.UtcNow)));
            Assert.Equal(420, reloaded.Experience);
            Assert.Equal(laborAfter, reloaded.LaborPower);
            Assert.Equal(420, Scalar($"SELECT experience FROM characters WHERE id={player.Id}"));
        }
    }

    [Theory]
    [InlineData(true, 0, 400)]
    [InlineData(true, 10, 0)]
    [InlineData(false, 0, 0)]
    public void ExperienceRecovery_RejectsMissingPaymentOrMissingLossWithoutASave(bool paidLabor, short labor, int recoverable)
    {
        using var graph = new SendGraph();
        using var services = new LaborRewardServices(graph.Sender, false);
        var player = graph.Sender;
        player.InitializeLaborCache(labor, DateTime.UtcNow);
        player.RecoverableExp = recoverable;
        var commits = 0;
        var skill = new Skill(new SkillTemplate { Id = 26611 })
            { CommitLaborBatch = (_, _) => { commits++; return true; } };
        var effect = new RecoverExpEffect { NeedLaborPower = paidLabor };
        Assert.False(SkillLaborBatch.Run(player, skill, true, () =>
            effect.Apply(player, null, player, null, null, new EffectSource(skill), null, DateTime.UtcNow)));
        Assert.Equal(0, commits);
        Assert.Equal(20, player.Experience);
        Assert.Equal(recoverable, player.RecoverableExp);
        Assert.Equal(labor, player.LaborPower);
    }
}
