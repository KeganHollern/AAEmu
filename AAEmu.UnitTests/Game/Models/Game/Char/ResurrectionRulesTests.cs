using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class ResurrectionRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task RepeatedDeaths_UseAuthoredWaitsAndRetainTheCounterAcrossReload()
    {
        var data = LoadData();
        var character = new CharacterMock { Hp = 0 };
        var waits = new[] { 0, 5000, 45000, 90000, 180000, 180000 };
        for (var index = 0; index < waits.Length; index++)
        {
            var now = Now.AddSeconds(index * 200);
            character.ComputeDeathWaitTime(now, KillReason.Damage, data);
            await Assert.That(character.RezWaitDuration).IsEqualTo(waits[index]);
            await Assert.That(character.RezPenaltyDuration).IsEqualTo(600000);
            await Assert.That(character.CanResurrect(now.AddMilliseconds(waits[index] - 1))).IsFalse();
            await Assert.That(character.CanResurrect(now.AddMilliseconds(waits[index]))).IsTrue();
            // These are the persisted fields used by both character load paths.
            character = new CharacterMock
            {
                Hp = 0, DeadCount = character.DeadCount, DeadTime = character.DeadTime,
                RezWaitDuration = character.RezWaitDuration, RezPenaltyDuration = character.RezPenaltyDuration
            };
        }
        await Assert.That(character.DeadCount).IsEqualTo((short)5);
        character.ComputeDeathWaitTime(character.DeadTime.AddSeconds(600), KillReason.Damage, data);
        await Assert.That(character.RezWaitDuration).IsEqualTo(0);
        await Assert.That(character.DeadCount).IsEqualTo((short)1);
    }

    [Test]
    public async Task SiegeDeath_UsesTheAuthoredSiegeColumn()
    {
        var data = LoadData();
        var character = new CharacterMock { Hp = 0 };
        foreach (var expected in new[] { 20000, 15000, 10000, 5000, 0 })
        {
            character.ComputeDeathWaitTime(character.DeadTime == default ? Now : character.DeadTime.AddSeconds(100),
                KillReason.PvpSiege, data);
            await Assert.That(character.RezWaitDuration).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task EarlyRequest_DoesNotConsumeTheOfferAndBoundaryRequestSucceedsOnce()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = Now, RezWaitDuration = 45000 };
        character.OfferResurrection(new SkillCasterUnit(2), 50, 25, offeredAt: Now);
        await Assert.That(character.TryBeginResurrection(true, Now.AddMilliseconds(44999), out _)).IsFalse();
        await Assert.That(character.RezTime).IsEqualTo(default(DateTime));
        await Assert.That(character.TryBeginResurrection(true, Now.AddMilliseconds(45000), out var offer)).IsTrue();
        await Assert.That(offer.GetHealth(100)).IsEqualTo(50);
        await Assert.That(character.RezTime).IsEqualTo(Now.AddMilliseconds(45000));
        await Assert.That(character.TryBeginResurrection(true, Now.AddMilliseconds(45000), out _)).IsFalse();
    }

    [Test]
    [Arguments(false)] [Arguments(true)]
    public async Task LivingCharacter_CannotStartEitherRevivalPath(bool inPlace)
    {
        var character = new CharacterMock { Hp = 1, DeadTime = Now.AddMinutes(-10) };
        await Assert.That(character.TryBeginResurrection(inPlace, Now, out _)).IsFalse();
        await Assert.That(character.RezTime).IsEqualTo(default(DateTime));
    }

    [Test]
    public async Task ExpiredOrPreviousDeathOffers_CannotAuthorizeInPlaceRevival()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = Now };
        await Assert.That(character.TryBeginResurrection(true, Now, out _)).IsFalse();
        character.OfferResurrection(new SkillCasterUnit(2), 50, 50, offeredAt: Now);
        await Assert.That(character.TryBeginResurrection(true, Now + Character.ResurrectionOfferLifetime, out _)).IsFalse();
        character.OfferResurrection(new SkillCasterUnit(2), 50, 50, offeredAt: Now);
        character.DeadTime = Now.AddSeconds(1);
        await Assert.That(character.TryBeginResurrection(true, Now.AddSeconds(1), out _)).IsFalse();
        await Assert.That(character.TryBeginResurrection(false, Now.AddSeconds(1), out _)).IsTrue();
    }

    [Test]
    public async Task ReplacementOffer_GetsItsOwnExpiryAndDoesNotRetainExpiredPriestExperience()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = Now };
        character.OfferResurrection(new SkillCasterUnit(2), 10, 10, restoreExperience: 500, offeredAt: Now);
        var replacementAt = Now + Character.ResurrectionOfferLifetime;
        character.OfferResurrection(new SkillCasterUnit(3), 50, 50, offeredAt: replacementAt);
        await Assert.That(character.TryTakeResurrectionOffer(out var offer, replacementAt.AddSeconds(1))).IsTrue();
        await Assert.That(offer.RestoreExperience).IsEqualTo(0);
        await Assert.That(offer.ExpiresAt).IsEqualTo(replacementAt + Character.ResurrectionOfferLifetime);
    }

    [Test]
    [Arguments(0)] [Arguments(2)] [Arguments(3)]
    public async Task Packet_RejectsTruncatedOrTrailingBytes(int count)
    {
        await Assert.That(CSResurrectCharacterPacket.TryReadRequest(new PacketStream(new byte[count]), out _)).IsFalse();
    }

    [Test]
    [Arguments((byte)0, true)] [Arguments((byte)1, true)] [Arguments((byte)2, false)] [Arguments((byte)255, false)]
    public async Task Packet_UsesExactlyOneBooleanByte(byte value, bool accepted)
    {
        var stream = new PacketStream(new[] { value });
        await Assert.That(CSResurrectCharacterPacket.TryReadRequest(stream, out var inPlace)).IsEqualTo(accepted);
        if (accepted)
        {
            await Assert.That(inPlace).IsEqualTo(value == 1);
            await Assert.That(stream.Pos).IsEqualTo(stream.Count);
        }
    }

    private static ResurrectionGameData LoadData()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE resurrection_waiting_times(id INTEGER, penalty_duration INTEGER, waiting_time INTEGER, siege_waiting_time INTEGER);
            INSERT INTO resurrection_waiting_times VALUES(5,600,180,0),(1,600,0,20),(3,600,45,10),(2,600,5,15),(4,600,90,5);
            """;
        command.ExecuteNonQuery();
        var data = new ResurrectionGameData();
        data.Load(connection);
        return data;
    }
}
