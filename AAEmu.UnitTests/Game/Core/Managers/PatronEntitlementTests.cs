using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.L2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class PatronEntitlementTests
{
    [Test]
    public async Task DefaultConnection_HasNoPatronEntitlement()
    {
        var payment = new GameConnection(null).Payment;
        await Assert.That(payment.PremiumState).IsFalse();
        await Assert.That(payment.Method).IsEqualTo(PaymentMethodType.None);
        await Assert.That(TimedRewardsManager.GetMaxLabor(payment.PremiumState)).IsEqualTo((short)2000);
    }

    [Test]
    public async Task ShippedCreditDefaults_DoNotGrantCreditsForEitherAccountState()
    {
        using var config = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Configurations", "CharacterSettings.json")).Build();
        var credits = config.GetSection("Credits").Get<AAEmu.Game.Models.Game.CurrencyValuesConfig>();
        await Assert.That(credits.GetTickAmount(false)).IsEqualTo(0);
        await Assert.That(credits.GetTickAmount(true)).IsEqualTo(0);
        await Assert.That(credits.DailyLogin).IsEqualTo(0);
        await Assert.That(credits.Default).IsEqualTo(0);
    }

    [Test]
    public async Task Admission_ParsesLoginGoldenBytes()
    {
        var bytes = Convert.FromHexString("2A00000078563412D4C3B2A100F153650000000000D2496B0000000000000000000000000000FFFFC0000201");
        await Assert.That(LGPlayerEnterPacket.TryReadAdmission(new PacketStream(bytes), out var account, out var connection, out var token, out var start, out var end, out var address)).IsTrue();
        await Assert.That(account).IsEqualTo(42u);
        await Assert.That(connection).IsEqualTo(0x12345678u);
        await Assert.That(token).IsEqualTo(0xa1b2c3d4u);
        await Assert.That(address.ToString()).IsEqualTo("192.0.2.1");
        await Assert.That(start).IsEqualTo(1700000000UL);
        await Assert.That(end).IsEqualTo(1800000000UL);
    }

    [Test]
    public async Task PatronState_UsesInclusiveStartAndExclusiveEnd()
    {
        var clock = new TestClock(999);
        var payment = new AccountPayment(1000, 2000, clock);
        await Assert.That(payment.PremiumState).IsFalse();
        clock.Seconds = 1000;
        await Assert.That(payment.PremiumState).IsTrue();
        await Assert.That(payment.Method).IsEqualTo(PaymentMethodType.Premium);
        clock.Seconds = 1999;
        await Assert.That(payment.PremiumState).IsTrue();
        clock.Seconds = 2000;
        await Assert.That(payment.PremiumState).IsFalse();
        await Assert.That(payment.Method).IsEqualTo(PaymentMethodType.None);
    }

    [Test]
    [Arguments(0UL, 0UL, true, 10)]
    [Arguments(0UL, 0UL, false, 0)]
    [Arguments(500UL, 2000UL, true, 20)]
    [Arguments(500UL, 2000UL, false, 10)]
    [Arguments(2000UL, 3000UL, true, 10)]
    [Arguments(2000UL, 3000UL, false, 0)]
    [Arguments(500UL, 1000UL, true, 10)]
    [Arguments(500UL, 1000UL, false, 0)]
    [Arguments(1300UL, 2000UL, true, 15)]
    [Arguments(1300UL, 2000UL, false, 5)]
    [Arguments(500UL, 1300UL, true, 15)]
    [Arguments(500UL, 1300UL, false, 5)]
    public async Task Labor_UsesOnlyTheEntitledPartOfTheInterval(ulong start, ulong end, bool online, int expected)
    {
        var amount = TimedRewardsManager.CalculateLabor(new AccountPayment(start, end), At(1000), At(1600), online, 5);
        await Assert.That(amount).IsEqualTo(expected);
    }

    [Test]
    public async Task Labor_InvalidIntervalOrIncompleteTickDoesNotGrant()
    {
        var payment = new AccountPayment(500, 2000);
        await Assert.That(TimedRewardsManager.CalculateLabor(payment, At(1000), At(1299), true, 5)).IsEqualTo(0);
        await Assert.That(TimedRewardsManager.CalculateLabor(payment, At(1000), At(1600), false, 0)).IsEqualTo(0);
        await Assert.That(TimedRewardsManager.CalculateLabor(payment, At(1600), At(1000), true, 5)).IsEqualTo(0);
    }

    [Test]
    public async Task BenefitsLoader_UsesBothCompactRows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE premium_benefits(grade_id INTEGER,online_labor INTEGER,offline_labor INTEGER,max_labor INTEGER); INSERT INTO premium_benefits VALUES(1,5,0,2000),(2,10,5,5000); CREATE TABLE premium_grades(grade_id INTEGER,point INTEGER); INSERT INTO premium_grades VALUES(1,0),(2,1)";
        command.ExecuteNonQuery();
        var data = new PremiumGameData();
        data.Load(connection);
        await Assert.That(data.Get(false)).IsEqualTo(new PremiumBenefits(5, 0, 2000));
        await Assert.That(data.Get(true)).IsEqualTo(new PremiumBenefits(10, 5, 5000));
        await Assert.That(data.GetPoint(false)).IsEqualTo(0);
        await Assert.That(data.GetPoint(true)).IsEqualTo(1);
    }

    [Test]
    [Arguments(8)] [Arguments(24)] [Arguments(43)] [Arguments(45)]
    public async Task Admission_RejectsOldTruncatedAndTrailingBodies(int length)
    {
        await Assert.That(LGPlayerEnterPacket.TryReadAdmission(new PacketStream(new byte[length]), out _, out _, out _, out _, out _, out _)).IsFalse();
    }

    [Test]
    [Arguments(0UL, 0UL, true)]
    [Arguments(1000UL, 2000UL, true)]
    [Arguments(0UL, 253402300799UL, true)]
    [Arguments(2000UL, 1000UL, false)]
    [Arguments(1000UL, 1000UL, false)]
    [Arguments(1000UL, 0UL, false)]
    [Arguments(0UL, 253402300800UL, false)]
    public async Task Admission_UsesExactUnsignedUtcSecondsAndValidatesBounds(ulong start, ulong end, bool expected)
    {
        var stream = new PacketStream();
        stream.Write(42u); stream.Write(0x12345678u); stream.Write(0xa1b2c3d4u); stream.Write(start); stream.Write(end);
        stream.Write(System.Net.IPAddress.Parse("192.0.2.1").MapToIPv6().GetAddressBytes());
        await Assert.That(LGPlayerEnterPacket.TryReadAdmission(stream, out var account, out var connection, out _, out var parsedStart, out var parsedEnd, out _)).IsEqualTo(expected);
        await Assert.That(account).IsEqualTo(42u);
        await Assert.That(connection).IsEqualTo(0x12345678u);
        await Assert.That(parsedStart).IsEqualTo(start);
        await Assert.That(parsedEnd).IsEqualTo(end);
    }

    [Test]
    public async Task AdmissionToken_KeepsEntitlementWithItsAccountAndConsumesOnce()
    {
        var manager = new EnterWorldManager(Mock.Of<IAccountManager>().Object, Mock.Of<IStreamManager>().Object,
            Mock.Of<IQuestManager>().Object, Mock.Of<IChatManager>().Object, Mock.Of<IFamilyManager>().Object,
            Mock.Of<IWorldManager>().Object);
        var payment = new AccountPayment(1000, 2000);
        manager.SetPendingAccount(7, 42, payment);
        await Assert.That(manager.ConsumePendingAccount(7, 43, out var rejected)).IsEqualTo(PendingWorldAccountResult.AccountMismatch);
        await Assert.That(rejected).IsNull();
        await Assert.That(manager.ConsumePendingAccount(7, 42, out var accepted)).IsEqualTo(PendingWorldAccountResult.Consumed);
        await Assert.That(accepted).IsSameReferenceAs(payment);
        await Assert.That(manager.ConsumePendingAccount(7, 42, out _)).IsEqualTo(PendingWorldAccountResult.NotFound);
    }

    private static DateTime At(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    private sealed class TestClock(long seconds) : TimeProvider
    {
        public long Seconds { get; set; } = seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Seconds);
    }
}
