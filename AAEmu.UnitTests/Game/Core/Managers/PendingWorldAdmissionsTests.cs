using System.Net;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.L2G;
using AAEmu.Game.Models;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class PendingWorldAdmissionsTests
{
    private static readonly IPAddress ClientAddress = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress OtherAddress = IPAddress.Parse("192.0.2.2");
    private const string Golden = "2A00000078563412D4C3B2A100F153650000000000D2496B0000000000000000000000000000FFFFC0000201";

    [Test]
    public async Task AdmissionPacket_RejectsEveryTruncationAndTrailingByte()
    {
        var bytes = Convert.FromHexString(Golden);
        for (var length = 0; length < bytes.Length; length++)
            await Assert.That(LGPlayerEnterPacket.TryReadAdmission(new PacketStream(bytes[..length]),
                out _, out _, out _, out _, out _, out _)).IsFalse();
        await Assert.That(LGPlayerEnterPacket.TryReadAdmission(new PacketStream([.. bytes, 0]),
            out _, out _, out _, out _, out _, out _)).IsFalse();
    }

    [Test]
    [Arguments(0)] [Arguments(4)] [Arguments(8)] [Arguments(28)]
    public async Task AdmissionPacket_RejectsZeroIdentifierOrUnspecifiedAddress(int offset)
    {
        var bytes = Convert.FromHexString(Golden);
        Array.Clear(bytes, offset, offset == 28 ? 16 : 4);
        await Assert.That(LGPlayerEnterPacket.TryReadAdmission(new PacketStream(bytes),
            out _, out _, out _, out _, out _, out _)).IsFalse();
    }

    [Test]
    public async Task Consume_AtExpiry_RejectsAndRemovesCookie()
    {
        var clock = new TestClock();
        var store = new PendingWorldAdmissions(clock);
        await Assert.That(store.TryAdd(1, 42, null, ClientAddress)).IsTrue();
        clock.Now += TimeSpan.FromSeconds(60);
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Expired);
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.NotFound);
    }

    [Test]
    public async Task Consume_BeforeExpiry_PreservesEntitlementAndSpendsOnce()
    {
        var clock = new TestClock();
        var store = new PendingWorldAdmissions(clock);
        var payment = new AccountPayment(1000, 2000);
        store.TryAdd(1, 42, payment, ClientAddress);
        clock.Now += TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1);
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out var accepted)).IsEqualTo(PendingWorldAccountResult.Consumed);
        await Assert.That(accepted).IsSameReferenceAs(payment);
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out var reused)).IsEqualTo(PendingWorldAccountResult.NotFound);
        await Assert.That(reused).IsNull();
    }

    [Test]
    public async Task Consume_WrongAccountOrAddress_DoesNotSpendVictimsCookie()
    {
        var store = new PendingWorldAdmissions();
        store.TryAdd(1, 42, null, ClientAddress);
        await Assert.That(store.Consume(1, 43, OtherAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.AccountMismatch);
        await Assert.That(store.Consume(1, 42, OtherAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.AddressMismatch);
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task Consume_MappedIPv4_MatchesTheIPv4Source()
    {
        var store = new PendingWorldAdmissions();
        store.TryAdd(1, 42, null, ClientAddress.MapToIPv6());
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task Consume_AddressCheckDisabledForTranslatedIngress_StillChecksAccountAndToken()
    {
        var store = new PendingWorldAdmissions();
        store.TryAdd(1, 42, null, ClientAddress);
        await Assert.That(store.Consume(1, 43, OtherAddress, false, out _)).IsEqualTo(PendingWorldAccountResult.AccountMismatch);
        await Assert.That(store.Consume(2, 42, OtherAddress, false, out _)).IsEqualTo(PendingWorldAccountResult.NotFound);
        await Assert.That(store.Consume(1, 42, OtherAddress, false, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task TryAdd_NewCookieForSameAccount_InvalidatesPreviousCookie()
    {
        var store = new PendingWorldAdmissions();
        store.TryAdd(1, 42, null, ClientAddress);
        await Assert.That(store.TryAdd(2, 42, null, ClientAddress)).IsTrue();
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.NotFound);
        await Assert.That(store.Consume(2, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task TryAdd_RandomTokenCollision_PreservesBothAccounts()
    {
        var store = new PendingWorldAdmissions();
        store.TryAdd(1, 42, null, ClientAddress);
        store.TryAdd(2, 43, null, OtherAddress);
        await Assert.That(store.TryAdd(1, 43, null, OtherAddress)).IsFalse();
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
        await Assert.That(store.Consume(2, 43, OtherAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task TryAdd_AfterExpiry_RemovesAbandonedCookie()
    {
        var clock = new TestClock();
        var store = new PendingWorldAdmissions(clock);
        store.TryAdd(1, 42, null, ClientAddress);
        clock.Now += TimeSpan.FromSeconds(60);
        await Assert.That(store.TryAdd(1, 43, null, OtherAddress)).IsTrue();
        await Assert.That(store.Consume(1, 43, OtherAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task Consume_ThreeFailures_BlocksOnlyThatAddressUntilExpiry()
    {
        var clock = new TestClock();
        var store = new PendingWorldAdmissions(clock);
        for (var i = 0; i < 3; i++)
            await Assert.That(store.Consume(100, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.NotFound);
        clock.Now += TimeSpan.FromSeconds(1);
        store.TryAdd(1, 42, null, ClientAddress);
        store.TryAdd(2, 43, null, OtherAddress);
        await Assert.That(store.Consume(1, 42, ClientAddress.MapToIPv6(), true, out _)).IsEqualTo(PendingWorldAccountResult.AddressBlocked);
        await Assert.That(store.Consume(2, 43, OtherAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
        clock.Now += TimeSpan.FromSeconds(59);
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.Consumed);
    }

    [Test]
    public async Task Consume_ConcurrentRequests_SpendsExactlyOnce()
    {
        var store = new PendingWorldAdmissions();
        store.TryAdd(1, 42, null, ClientAddress);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() => store.Consume(1, 42, ClientAddress, true, out _))));
        await Assert.That(results.Count(result => result == PendingWorldAccountResult.Consumed)).IsEqualTo(1);
        await Assert.That(results.Count(result => result == PendingWorldAccountResult.NotFound)).IsEqualTo(1);
    }

    [Test]
    public async Task Consume_ManyDistinctAddresses_KeepsARecentBlockedAddress()
    {
        var store = new PendingWorldAdmissions();
        for (var i = 0; i <= PendingWorldAdmissions.MaxFailureAddresses; i++)
            store.Consume(1, 42, new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }), true, out _);
        for (var i = 0; i < 3; i++)
            store.Consume(1, 42, ClientAddress, true, out _);
        await Assert.That(store.Consume(1, 42, ClientAddress, true, out _)).IsEqualTo(PendingWorldAccountResult.AddressBlocked);
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
