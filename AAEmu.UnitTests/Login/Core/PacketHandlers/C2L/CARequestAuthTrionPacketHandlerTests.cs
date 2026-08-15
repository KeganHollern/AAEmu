#nullable enable

using System.Net;
using AAEmu.Login.Core.Authentication;
using AAEmu.Login.Core.Controllers;
using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Core.Network.Connections;
using AAEmu.Login.Core.PacketHandlers.C2L;
using AAEmu.Login.Core.Packets.C2L;
using AAEmu.Login.Core.Services;
using AAEmu.Login.Models;

namespace AAEmu.UnitTests.Login.Core.PacketHandlers.C2L;

public class CARequestAuthTrionPacketHandlerTests
{
    private readonly Mock<ILoginController> _loginController = Mock.Of<ILoginController>();
    private readonly Mock<ILaunchTicketService> _launchTicketService = Mock.Of<ILaunchTicketService>();
    private readonly Mock<ILoginSession> _session = Mock.Of<ILoginSession>();
    private readonly Mock<ILoginConnection> _connection = Mock.Of<ILoginConnection>();
    private readonly CARequestAuthTrionPacketHandler _handler;

    public CARequestAuthTrionPacketHandlerTests()
    {
        _handler = new CARequestAuthTrionPacketHandler(_loginController.Object, _launchTicketService.Object);

        _launchTicketService.ConsumeAsync(Any<string>(), Any<string>(), Any<CancellationToken>())
            .Returns((ConsumedLaunchTicket?)null);

        _connection.Ip.Returns(IPAddress.Loopback);
        _session.Connection.Returns(_connection.Object);
    }

    [Test]
    public async Task Execute_CallsAuthenticateAsync()
    {
        // Arrange
        var packet = CreatePacket("testuser", "testpass");

        // Act
        await _handler.Execute(packet, _session.Object, CancellationToken.None);

        // Assert
        _session.AuthenticateAsync(Any<IAuthenticationFlow>(), Any<CancellationToken>()).WasCalled(Times.Once);
    }

    [Test]
    public async Task Execute_ValidLaunchTicket_UsesLauncherFlowWithoutPasswordFallback()
    {
        const string Username = "testuser";
        const string Ticket = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var accountId = new AccountId(42);
        IAuthenticationFlow? capturedFlow = null;
        _launchTicketService.ConsumeAsync(Username, Ticket, Any<CancellationToken>())
            .Returns(new ConsumedLaunchTicket(accountId, Username));
        _session.AuthenticateAsync(Any<IAuthenticationFlow>(), Any<CancellationToken>())
            .Callback((flow, _) => capturedFlow = flow);

        await _handler.Execute(CreatePacket(Username, Ticket), _session.Object, CancellationToken.None);

        await Assert.That(capturedFlow).IsTypeOf<LauncherTicketAuthFlow>();
        _loginController.Login(Any<string>(), Any<Password>(), Any<IPAddress>(), Any<CancellationToken>())
            .WasCalled(Times.Never);
    }

    private static CARequestAuthTrionPacket CreatePacket(string username, string password)
    {
        var packet = new CARequestAuthTrionPacket();
        var usernameProperty = typeof(CARequestAuthTrionPacket).GetProperty(nameof(CARequestAuthTrionPacket.Username));
        var passwordProperty = typeof(CARequestAuthTrionPacket).GetProperty(nameof(CARequestAuthTrionPacket.Password));
        usernameProperty!.SetValue(packet, username);
        passwordProperty!.SetValue(packet, password);
        return packet;
    }
}
