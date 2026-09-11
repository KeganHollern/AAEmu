using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Packets;

[NotInParallel]
public class ModerationEnforcementTests
{
    [Test]
    [Arguments(ChatType.White)]
    [Arguments(ChatType.Whisper)]
    [Arguments(ChatType.Party)]
    [Arguments(ChatType.Raid)]
    [Arguments(ChatType.Clan)]
    [Arguments(ChatType.Family)]
    [Arguments(ChatType.Region)]
    [Arguments(ChatType.Ally)]
    [Arguments(ChatType.Judge)]
    [Arguments(ChatType.Trade)]
    [Arguments(ChatType.Shout)]
    [Arguments(ChatType.GroupFind)]
    public async Task MutedChat_StopsBeforeAnyChannelDispatch(ChatType type)
    {
        var field = typeof(Singleton<ModerationManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            var manager = new ModerationManager(Mock.Of<IPermissionManager>().Object, _ => false,
                () => [], (_, _) => { }, TimeProvider.System, TimeSpan.FromSeconds(1));
            manager.ApplyState(new ModerationState(20, 1, false, 0, true, 0));
            field.SetValue(null, manager);
            var session = new RecordingSession();
            var connection = new GameConnection(session);
            connection.TryAuthenticate(20);
            connection.ActiveChar = new Character(new UnitCustomModelParams()) { AccountId = 20, Connection = connection };
            var body = new PacketStream().Write((short)type).Write((short)0).Write(0)
                .Write("target").Write("muted text").Write((byte)0).Write(0);
            body.Rollback();

            // No world/channel/spam manager exists. Reaching those paths fails this test.
            new CSSendChatMessagePacket { Connection = connection }.Read(body);

            await Assert.That(session.Packets.Count).IsEqualTo(1);
            await Assert.That(body.LeftBytes).IsEqualTo(0);
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }

    [Test]
    public async Task CooldownOverride_DemotionDisablesActiveFlag()
    {
        var field = typeof(Singleton<PermissionManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            var accounts = Mock.Of<IAccountManager>();
            accounts.GetAccountRole(20).Returns(AccountRole.Moderator);
            field.SetValue(null, new PermissionManager(accounts.Object));
            var character = new Character(new UnitCustomModelParams()) { AccountId = 20, IgnoreSkillCooldowns = true };
            await Assert.That(Skill.CanIgnoreCooldowns(character)).IsTrue();

            accounts.GetAccountRole(20).Returns(AccountRole.NormalPlayer);
            await Assert.That(Skill.CanIgnoreCooldowns(character)).IsFalse();
            await Assert.That(character.IgnoreSkillCooldowns).IsTrue();
            await Assert.That(Skill.CanIgnoreCooldowns(null)).IsFalse();
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }

    private sealed class RecordingSession : ISession
    {
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
