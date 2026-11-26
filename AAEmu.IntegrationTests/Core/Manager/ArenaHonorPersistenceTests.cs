using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GladiatorHonor_PaysTheOnlinePlayerAndSettlesTheOtherPlayerAtEntryOnce(bool draw)
    {
        using var graph = new SendGraph();
        var red = graph.Receiver;
        var blue = graph.Sender;
        var client = new Mock<ISession>();
        graph.ConnectReceiver(client);
        red.HonorPoint = 77;
        blue.HonorPoint = 23;
        Assert.True(graph.Save.TryCommitEconomy([red, blue]));

        red.HonorPoint += 3; // Ordinary Honor earned since the last save.
        var match = Guid.NewGuid();
        var redHonor = draw ? 5 : 20;
        var rank = new GladiatorRankResult(match, new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc),
            red.Id, draw ? 30 : 60, draw ? 0 : 4, blue.Id, 30, draw ? 0 : 1);
        var result = new ArenaMatchResult(match, 11, rank, [new(red.Id, redHonor), new(blue.Id, 5)]);
        await SaveArenaResult(result);
        await SaveArenaResult(result); // Retry after loss of a match acknowledgement.

        Assert.Equal(2, ArenaReceipts(match));
        Assert.Equal(1, ArenaReceipts(match, claimedOnly: true));
        Assert.Equal(80 + redHonor, red.HonorPoint);
        Assert.Equal(red.HonorPoint, SavedHonor(red));
        Assert.Equal(23, blue.HonorPoint);
        Assert.Equal(23, SavedHonor(blue));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM gladiator_rank_results WHERE match_id='{match:D}' AND corps1_rank_points={rank.Corps1RankPoints} AND corps2_rank_points={rank.Corps2RankPoints}"));
        client.Verify(session => session.SendPacket(It.Is<byte[]>(packet => packet.Length >= 8 &&
            BitConverter.ToUInt16(packet, 6) == SCOffsets.SCCharacterGamePointsPacket)), Times.Once);

        graph.Save.ClaimArenaHonorOnEntry(blue);
        graph.Save.ClaimArenaHonorOnEntry(blue);
        Assert.True(graph.Save.TryCommitEconomy([red, blue]));
        Assert.Equal(2, ArenaReceipts(match, claimedOnly: true));
        Assert.Equal(28, blue.HonorPoint);
        Assert.Equal(28, SavedHonor(blue));
        Assert.Equal(red.HonorPoint, SavedHonor(red));
    }

    [Fact]
    public async Task DrillCampHonor_FailedClaimKeepsTheReceiptAndBothBalancesUntilRetry()
    {
        using var graph = new SendGraph();
        var red = graph.Receiver;
        var blue = graph.Sender;
        graph.ConnectReceiver(new Mock<ISession>());
        var match = Guid.NewGuid();
        // These totals include two positive category leads on red, and one tied lead on blue.
        var result = new ArenaMatchResult(match, 7, null, [new(red.Id, 70), new(blue.Id, 20)]);
        var trigger = $"arena_honor_fail_{red.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE UPDATE ON arena_match_honor FOR EACH ROW BEGIN IF NEW.character_id={red.Id} AND NEW.claimed=1 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='private failed claim'; END IF; END");
        try
        {
            await SaveArenaResult(result);
            Assert.Equal(2, ArenaReceipts(match));
            Assert.Equal(0, ArenaReceipts(match, claimedOnly: true));
            Assert.Equal(0, red.HonorPoint);
            Assert.Equal(0, SavedHonor(red));
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}");
        }
        graph.Save.DeliverArenaHonor(result.Awards);
        graph.Save.ClaimArenaHonorOnEntry(blue);
        await SaveArenaResult(result);
        Assert.True(graph.Save.TryCommitEconomy([red, blue]));
        Assert.Equal(2, ArenaReceipts(match, claimedOnly: true));
        Assert.Equal(70, red.HonorPoint);
        Assert.Equal(70, SavedHonor(red));
        Assert.Equal(20, blue.HonorPoint);
        Assert.Equal(20, SavedHonor(blue));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM gladiator_rank_results WHERE match_id='{match:D}'"));
    }

    [Fact]
    public async Task ArenaHonor_ClaimBeforeEntryLoadsBuffsAndCooldownsKeepsBothSavedRows()
    {
        using var graph = new SendGraph();
        var player = graph.Sender;
        var result = new ArenaMatchResult(Guid.NewGuid(), 7, null, [new(player.Id, 10)]);
        await SaveArenaResult(result);
        Execute($"INSERT INTO character_active_buffs(character_id,buff_id,caster_id,duration,time_left) VALUES({player.Id},239,{player.Id},1800000,300000)");
        Execute($"INSERT INTO character_cooldowns(character_id,skill_id,duration_ms,expires_at) VALUES({player.Id},11715,90000,UTC_TIMESTAMP(3)+INTERVAL 90 SECOND)");

        graph.Save.ClaimArenaHonorOnEntry(player);
        Assert.Equal(10, SavedHonor(player));
        Assert.Equal(10, player.HonorPoint);
        Assert.Equal(300000, Scalar($"SELECT time_left FROM character_active_buffs WHERE character_id={player.Id} AND buff_id=239"));
        Assert.Equal(90000, Scalar($"SELECT duration_ms FROM character_cooldowns WHERE character_id={player.Id} AND skill_id=11715"));
    }

    [Fact]
    public void GladiatorHonor_SecondAwardWriteFailureRollsBackRankAndBothAwards()
    {
        using var graph = new SendGraph();
        var match = Guid.NewGuid();
        var red = graph.Sender;
        var blue = graph.Receiver;
        var rank = new GladiatorRankResult(match, DateTime.UtcNow, red.Id, 60, 4, blue.Id, 0, 1);
        var result = new ArenaMatchResult(match, 11, rank, [new(red.Id, 20), new(blue.Id, 5)]);
        var trigger = $"arena_honor_result_fail_{blue.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON arena_match_honor FOR EACH ROW BEGIN IF NEW.character_id={blue.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='private failed result'; END IF; END");
        try
        {
            Assert.False(graph.Save.TryCommitEconomy([], result.Write));
            Assert.Equal(0, ArenaReceipts(match));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM gladiator_rank_results WHERE match_id='{match:D}'"));
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}");
        }
        Assert.True(graph.Save.TryCommitEconomy([], result.Write));
        Assert.Equal(2, ArenaReceipts(match));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM gladiator_rank_results WHERE match_id='{match:D}'"));
    }

    private static Task SaveArenaResult(ArenaMatchResult result)
    {
        lock (SaveManager.PersistenceSyncRoot)
            return InstantGameManager.Instance.RecordMatchResult(result).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static long SavedHonor(Character character) =>
        Scalar($"SELECT honor_point FROM characters WHERE id={character.Id}");

    private static long ArenaReceipts(Guid match, bool claimedOnly = false) =>
        Scalar($"SELECT COUNT(*) FROM arena_match_honor WHERE match_id='{match:D}'" + (claimedOnly ? " AND claimed=1" : ""));
}
