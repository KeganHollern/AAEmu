#nullable enable

using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Login.Core.Launcher;

public class LaunchTicketServiceTests
{
    private sealed class MemoryTicketStore : ILaunchTicketStore
    {
        private readonly Dictionary<string, StoredLaunchTicket> _tickets = [];
        private readonly Dictionary<ulong, string> _sessions = [];

        public Task<bool> StoreAsync(LauncherSessionPrincipal principal, byte[] ticketHash,
            DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            if (_sessions.Remove(principal.SessionId, out var previousHash))
                _tickets.Remove(previousHash);
            var hash = Convert.ToHexStringLower(ticketHash);
            _tickets[hash] = new StoredLaunchTicket(
                principal.SessionId, principal.AccountId, principal.Username, expiresAt);
            _sessions[principal.SessionId] = hash;
            return Task.FromResult(true);
        }

        public Task<StoredLaunchTicket?> ConsumeAsync(byte[] ticketHash, CancellationToken cancellationToken)
        {
            var hash = Convert.ToHexStringLower(ticketHash);
            if (!_tickets.Remove(hash, out var ticket))
                return Task.FromResult<StoredLaunchTicket?>(null);
            _sessions.Remove(ticket.SessionId);
            return Task.FromResult<StoredLaunchTicket?>(ticket);
        }
    }

    private static readonly AccountId s_accountId = new(42);
    private readonly Mock<ILauncherSessionService> _sessionService = Mock.Of<ILauncherSessionService>();
    private readonly FakeTimeProvider _timeProvider = new();

    private LaunchTicketService CreateService()
    {
        _sessionService.IsActiveAsync(Any<ulong>(), Any<AccountId>(), Any<CancellationToken>()).Returns(true);
        return new LaunchTicketService(
            _sessionService.Object,
            new MemoryTicketStore(),
            Options.Create(new LauncherApiOptions { Enabled = true, LaunchTicketLifetimeSeconds = 60 }),
            _timeProvider);
    }

    [Test]
    public async Task ConsumeAsync_IssuedTicket_ReturnsAccountOnce()
    {
        var service = CreateService();
        var principal = new LauncherSessionPrincipal(7, s_accountId, "alice");
        var issued = await service.IssueAsync(principal, CancellationToken.None);

        await Assert.That(issued).IsNotNull();
        await Assert.That(issued!.Ticket.Length).IsEqualTo(64);
        var first = await service.ConsumeAsync("alice", issued.Ticket, CancellationToken.None);
        var second = await service.ConsumeAsync("alice", issued.Ticket, CancellationToken.None);

        await Assert.That(first).IsEqualTo(new ConsumedLaunchTicket(s_accountId, "alice"));
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task ConsumeAsync_ExpiredTicket_ReturnsNull()
    {
        var service = CreateService();
        var issued = await service.IssueAsync(
            new LauncherSessionPrincipal(7, s_accountId, "alice"), CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromSeconds(61));

        var consumed = await service.ConsumeAsync("alice", issued!.Ticket, CancellationToken.None);

        await Assert.That(consumed).IsNull();
    }

    [Test]
    public async Task IssueAsync_SameSession_InvalidatesPreviousTicket()
    {
        var service = CreateService();
        var principal = new LauncherSessionPrincipal(7, s_accountId, "alice");
        var first = await service.IssueAsync(principal, CancellationToken.None);
        var second = await service.IssueAsync(principal, CancellationToken.None);

        var oldTicket = await service.ConsumeAsync("alice", first!.Ticket, CancellationToken.None);
        var currentTicket = await service.ConsumeAsync("alice", second!.Ticket, CancellationToken.None);

        await Assert.That(oldTicket).IsNull();
        await Assert.That(currentTicket).IsNotNull();
    }

    [Test]
    public async Task ConsumeAsync_ApiDisabled_DoesNotAccessTicketStore()
    {
        var ticketStore = Mock.Of<ILaunchTicketStore>();
        var service = new LaunchTicketService(
            _sessionService.Object,
            ticketStore.Object,
            Options.Create(new LauncherApiOptions { Enabled = false }),
            _timeProvider);

        var consumed = await service.ConsumeAsync(
            "legacy-user",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            CancellationToken.None);

        await Assert.That(consumed).IsNull();
        ticketStore.ConsumeAsync(Any<byte[]>(), Any<CancellationToken>()).WasCalled(Times.Never);
    }
}
