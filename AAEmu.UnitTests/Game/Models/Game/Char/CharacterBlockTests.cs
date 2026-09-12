using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class CharacterBlockTests
{
    [Test]
    public async Task OfflineNames_CanBeBlockedAndRemovedWithoutAnOnlineCharacter()
    {
        var field = typeof(Singleton<NameManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var names = new NameManager();
        names.Load([], [], []);
        names.AddCharacter(1, "Owner", 1);
        names.AddCharacter(2, "Offline", 2);
        field.SetValue(null, names);
        try
        {
            var (owner, _) = Character(1);
            owner.Blocked.AddBlockedUser("offline");
            owner.Blocked.AddBlockedUser("OFFLINE");
            owner.Blocked.AddBlockedUser("Owner");
            await Assert.That(owner.Blocked.BlockedList.Count).IsEqualTo(1);
            await Assert.That(owner.Blocked.Contains(2)).IsTrue();
            await Assert.That(owner.Blocked.Contains(1)).IsFalse();
            owner.Blocked.RemoveBlockedUser("OFFLINE");
            owner.Blocked.RemoveBlockedUser("Offline");
            await Assert.That(owner.Blocked.Contains(2)).IsFalse();
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void Whisper_UsesTheReceiversBlockList(bool receiverBlocks)
    {
        var (sender, _) = Character(1);
        var (receiver, session) = Character(2);
        Block(receiverBlocks ? receiver : sender, receiverBlocks ? sender : receiver);
        CSSendChatMessagePacket.SendWhisper(sender, receiver, "Message", 0, 0);
        session.SendPacket(Any<byte[]>()).WasCalled(receiverBlocks ? Times.Never : Times.Once);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void FamilyInvitation_UsesTheReceiversBlockList(bool receiverBlocks)
    {
        var (sender, _) = Character(1);
        var (receiver, session) = Character(2);
        Block(receiverBlocks ? receiver : sender, receiverBlocks ? sender : receiver);
        var world = Mock.Of<IWorldManager>();
        world.GetCharacter(receiver.Name).Returns(receiver);
        var families = new FamilyManager(world.Object, Mock.Of<IChatManager>().Object, Mock.Of<IFamilyIdManager>().Object);
        families.InviteToFamily(sender, receiver.Name, "Member");
        session.SendPacket(Any<byte[]>()).WasCalled(receiverBlocks ? Times.Never : Times.Once);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void ExpeditionInvitation_UsesTheReceiversBlockList(bool receiverBlocks)
    {
        var (sender, _) = Character(1);
        var (receiver, session) = Character(2);
        Block(receiverBlocks ? receiver : sender, receiverBlocks ? sender : receiver);
        sender.Expedition = new Expedition
        {
            Name = "Guild",
            Members = [new ExpeditionMember { CharacterId = sender.Id, Role = 1 }],
            Policies = [new ExpeditionRolePolicy { Role = 1, Invite = true }]
        };
        var world = Mock.Of<IWorldManager>();
        world.GetCharacter(receiver.Name).Returns(receiver);
        var expeditions = new ExpeditionManager(Mock.Of<IExpeditionIdManager>().Object,
            Mock.Of<ITeamManager>().Object, world.Object, Mock.Of<IChatManager>().Object);
        RegisterGuild(expeditions, sender.Expedition);
        expeditions.Invite(sender.Connection, receiver.Name);
        session.SendPacket(Any<byte[]>()).WasCalled(receiverBlocks ? Times.Never : Times.Once);
    }

    [Test]
    [Arguments("no_invitation")]
    [Arguments("wrong_inviter")]
    [Arguments("wrong_guild")]
    [Arguments("blocked_after_invite")]
    [Arguments("permission_removed")]
    [Arguments("inviter_left")]
    [Arguments("replaced_character")]
    [Arguments("expired")]
    [Arguments("declined")]
    public async Task ExpeditionReply_RejectsForgedOrInvalidatedInvitations(string reason)
    {
        var (sender, _) = Character(1);
        var (receiver, session) = Character(2);
        var guild = new Expedition
        {
            Id = (FactionsEnum)1000, Name = "Guild",
            Members = [new ExpeditionMember { CharacterId = sender.Id, Role = 1 }],
            Policies = [new ExpeditionRolePolicy { Role = 1, Invite = true }]
        };
        sender.Expedition = guild;
        var world = Mock.Of<IWorldManager>();
        world.GetCharacter(receiver.Name).Returns(receiver);
        var manager = new ExpeditionManager(Mock.Of<IExpeditionIdManager>().Object,
            Mock.Of<ITeamManager>().Object, world.Object, Mock.Of<IChatManager>().Object);
        typeof(ExpeditionManager).GetField("_expeditions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<FactionsEnum, Expedition> { [guild.Id] = guild });
        RegisterGuild(manager, sender.Expedition);
        var now = DateTime.UtcNow;
        manager.InvitationTime = () => now;
        if (reason != "no_invitation")
            manager.Invite(sender.Connection, receiver.Name);
        if (reason == "blocked_after_invite") Block(receiver, sender);
        if (reason == "permission_removed") guild.Policies[0].Invite = false;
        if (reason == "inviter_left") sender.Expedition = null;
        if (reason == "expired") now = now.AddMinutes(1);
        if (reason == "declined") manager.ReplyInvite(receiver.Connection, guild.Id, sender.Id, false);
        var connection = receiver.Connection;
        if (reason == "replaced_character")
        {
            var (replacement, _) = Character(receiver.Id);
            connection = replacement.Connection;
        }
        manager.ReplyInvite(connection, reason == "wrong_guild" ? (FactionsEnum)1001 : guild.Id,
            reason == "wrong_inviter" ? 3u : sender.Id, true);
        await Assert.That(receiver.Expedition).IsNull();
        await Assert.That(connection.ActiveChar.Expedition).IsNull();
        await Assert.That(guild.Members.Count).IsEqualTo(1);
        session.SendPacket(Any<byte[]>()).WasCalled(reason == "no_invitation" ? Times.Never : Times.Once);
    }

    [Test]
    public void ExpeditionInvitation_KeepsOnePendingRequestAndAllowsAnotherAfterExpiry()
    {
        var (sender, _) = Character(1);
        var (receiver, session) = Character(2);
        sender.Expedition = new Expedition
        {
            Name = "Guild", Members = [new ExpeditionMember { CharacterId = sender.Id, Role = 1 }],
            Policies = [new ExpeditionRolePolicy { Role = 1, Invite = true }]
        };
        var world = Mock.Of<IWorldManager>();
        world.GetCharacter(receiver.Name).Returns(receiver);
        var manager = new ExpeditionManager(Mock.Of<IExpeditionIdManager>().Object,
            Mock.Of<ITeamManager>().Object, world.Object, Mock.Of<IChatManager>().Object);
        RegisterGuild(manager, sender.Expedition);
        var now = DateTime.UtcNow;
        manager.InvitationTime = () => now;
        manager.Invite(sender.Connection, receiver.Name);
        manager.Invite(sender.Connection, receiver.Name);
        session.SendPacket(Any<byte[]>()).WasCalled(Times.Once);
        now = now.AddMinutes(1);
        manager.Invite(sender.Connection, receiver.Name);
        session.SendPacket(Any<byte[]>()).WasCalled(Times.Exactly(2));
    }

    private static void RegisterGuild(ExpeditionManager manager, Expedition guild) =>
        typeof(ExpeditionManager).GetField("_expeditions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<FactionsEnum, Expedition> { [guild.Id] = guild });

    private static void Block(Character blocker, Character blocked) =>
        blocker.Blocked.BlockedList.Add(blocked.Id, new BlockedTemplate { Owner = blocker.Id, BlockedId = blocked.Id });

    private static (CharacterMock, Mock<ISession>) Character(uint id)
    {
        var session = Mock.Of<ISession>();
        var character = new CharacterMock
        {
            Id = id, Name = $"Character{id}", Faction = new SystemFaction(),
            Connection = new GameConnection(session.Object)
        };
        character.Connection.ActiveChar = character;
        character.Blocked = new CharacterBlocked(character);
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(character, true);
        return (character, session);
    }
}
