using System.Numerics;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class PriestPurchaseTests
{
    [Test]
    [Arguments((byte)1, 100)]
    [Arguments((byte)10, 1000)]
    [Arguments((byte)50, 5000)]
    [Arguments((byte)55, 5500)]
    public async Task AuthoredPrice_IsCopperPerCharacterLevel(byte level, int expected)
    {
        var row = new PriestBuffOffer(1, 239, 100);
        await Assert.That(row.TryGetCost(level, out var cost)).IsTrue();
        await Assert.That(cost).IsEqualTo(expected);
    }

    [Test]
    public async Task Loader_UsesTheExactAuthoredOfferIdentifierAndCost()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE priest_buffs(id INTEGER,buff_id INTEGER,cost INTEGER); INSERT INTO priest_buffs VALUES(1,239,100)";
        command.ExecuteNonQuery();
        var data = new PriestBuffGameData();
        data.Load(connection);
        await Assert.That(data.Get(1)).IsEqualTo(new PriestBuffOffer(1, 239, 100));
        await Assert.That(data.Get(239)).IsNull();
    }

    [Test]
    [Arguments(0)] [Arguments(6)] [Arguments(8)]
    public async Task Packet_RejectsAnyOtherBodyLength(int length)
    {
        await Assert.That(CSBuyPriestBuffPacket.TryReadRequest(new PacketStream(new byte[length]), out _, out _)).IsFalse();
    }

    [Test]
    public async Task Packet_ReadsRowIdAndFixedThreeByteNpcId()
    {
        var stream = new PacketStream(new byte[] { 1, 0, 0, 0, 0x56, 0x34, 0x12 });
        await Assert.That(CSBuyPriestBuffPacket.TryReadRequest(stream, out var row, out var npc)).IsTrue();
        await Assert.That(row).IsEqualTo(1u);
        await Assert.That(npc).IsEqualTo(0x123456u);
        await Assert.That(stream.Pos).IsEqualTo(stream.Count);
    }

    [Test]
    public async Task InPlaceResurrection_NeedsOneCurrentDeathOffer()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = DateTime.UtcNow };
        await Assert.That(character.TryTakeResurrectionOffer(out _)).IsFalse();
        character.OfferResurrection(new SkillCasterUnit(1), 10, 10, true, 500);
        await Assert.That(character.TryTakeResurrectionOffer(out var offer)).IsTrue();
        await Assert.That(offer.GetHealth(1234)).IsEqualTo(123);
        await Assert.That(offer.GetMana(4321)).IsEqualTo(432);
        await Assert.That(character.TryTakeResurrectionOffer(out _)).IsFalse();
        character.OfferResurrection(new SkillCasterUnit(1), 10, 10);
        character.DeadTime = character.DeadTime.AddSeconds(1);
        await Assert.That(character.TryTakeResurrectionOffer(out _)).IsFalse();
    }

    [Test]
    public async Task PriestOffer_RestoresOnlyThisDeathLossAndPlayerSkillKeepsThatBenefit()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = DateTime.UtcNow, LastExpLoss = 500, RecoverableExp = 400 };
        typeof(Character).GetProperty(nameof(Character.Experience))!.SetValue(character, 1000);
        character.Transform.World.SetPosition(100, 200, 30);
        character.OfferResurrection(new SkillCasterUnit(1), 10, 10, true, 500);
        character.OfferResurrection(new SkillCasterUnit(2), 50, 50);
        await Assert.That(character.TryTakeResurrectionOffer(out var offer)).IsTrue();
        await Assert.That(offer.Position).IsEqualTo(new Vector3(100, 200, 30));
        character.RestorePriestExperience(offer);
        await Assert.That(character.Experience).IsEqualTo(1500);
        await Assert.That(character.LastExpLoss).IsEqualTo(0);
        await Assert.That(character.RecoverableExp).IsEqualTo(0);
        character.RestorePriestExperience(offer);
        await Assert.That(character.Experience).IsEqualTo(1500);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PurchaseCheckpoint_CommitsBothCoinAndBuffOrRestoresBoth(bool success)
    {
        var character = new CharacterMock { Money = 5000 };
        var active = false;
        var checkpointSawBoth = false;
        var result = character.CompletePriestPurchase(5000, () => active = true, () => active = false, () =>
        {
            checkpointSawBoth = character.Money == 0 && active;
            return success;
        });
        await Assert.That(checkpointSawBoth).IsTrue();
        await Assert.That(result).IsEqualTo(success);
        await Assert.That(active).IsEqualTo(success);
        await Assert.That(character.Money).IsEqualTo(success ? 0L : 5000L);
    }

    [Test]
    public async Task InsufficientCoin_DoesNotApplyBuffOrReachCheckpoint()
    {
        var character = new CharacterMock { Money = 4999 };
        var applied = false;
        var committed = false;
        var result = character.CompletePriestPurchase(5000, () => applied = true, () => applied = false,
            () => committed = true);
        await Assert.That(result).IsFalse();
        await Assert.That(applied).IsFalse();
        await Assert.That(committed).IsFalse();
        await Assert.That(character.Money).IsEqualTo(4999L);
    }

    [Test]
    public async Task ApplyFailure_RemovesPartialBuffAndRestoresCoin()
    {
        var character = new CharacterMock { Money = 5000 };
        var active = false;
        try
        {
            character.CompletePriestPurchase(5000, () =>
            {
                active = true;
                throw new InvalidOperationException("Injected buff apply failure");
            }, () => active = false, () => true);
        }
        catch (InvalidOperationException) { }
        await Assert.That(active).IsFalse();
        await Assert.That(character.Money).IsEqualTo(5000L);
    }

    [Test]
    public async Task ExpiredBuff_CannotProvideResurrectionWhileItsTimerWaits()
    {
        var start = DateTime.UtcNow;
        var buff = new Buff(null, null, new SkillCasterUnit(1), new BuffTemplate { ResurrectionHealth = 10 }, null, start)
        {
            Duration = 1800000, State = EffectState.Acting
        };
        await Assert.That(Character.HasActivePriestResurrection(buff, start.AddMilliseconds(1799999))).IsTrue();
        await Assert.That(Character.HasActivePriestResurrection(buff, start.AddMilliseconds(1800000))).IsFalse();
    }

    [Test]
    public async Task LivingCharacter_CannotUseOrCreateAResurrectionOffer()
    {
        var character = new CharacterMock { Hp = 10, DeadTime = DateTime.UtcNow };
        character.OfferResurrection(new SkillCasterUnit(1), 10, 10);
        await Assert.That(character.TryTakeResurrectionOffer(out _)).IsFalse();
    }
}
