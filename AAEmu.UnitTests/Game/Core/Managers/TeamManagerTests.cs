using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Utils.Mocks;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class TeamManagerTests
{
    private static readonly FieldInfo s_achievementsField =
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_inPartyField =
        typeof(Character).GetField("<InParty>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockWorld = Mock.Of<IWorldManager>();
        var mockChat = Mock.Of<IChatManager>();
        var mockTeamId = Mock.Of<ITeamIdManager>();
        var manager = new TeamManager(mockWorld.Object, mockChat.Object, mockTeamId.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockWorld);
        Mock.VerifyNoOtherCalls(mockChat);
        Mock.VerifyNoOtherCalls(mockTeamId);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecipientBlock_PreventsDirectAndPendingInvitation(bool alreadyInvited)
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Owner");
        var target = CreateCharacter(2, "Target");
        if (alreadyInvited)
            manager.AskToJoin(owner, target.Name, 0, true, target);
        target.Blocked = new CharacterBlocked(target);
        target.Blocked.BlockedList[owner.Id] = new BlockedTemplate { Owner = target.Id, BlockedId = owner.Id };
        if (alreadyInvited)
            manager.ReplyToJoinTeam(target, 0, true, owner.Id, false, target.Name, false);
        else
            manager.AskToJoin(owner, target.Name, 0, true, target);
        await Assert.That(GetActiveInvitations(manager)).IsEmpty();
        await Assert.That(GetActiveTeams(manager)).IsEmpty();
    }

    [Test]
    public async Task AskToJoin_TargetInActiveTeam_DoesNotCreateInvitation()
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Owner");
        var target = CreateCharacter(2, "Target");
        var targetTeam = CreateTeam(20, target);
        GetActiveTeams(manager)[targetTeam.Id] = targetTeam;

        manager.AskToJoin(owner, target.Name, 0, true, target);

        await Assert.That(GetActiveInvitations(manager)).IsEmpty();
    }

    [Test]
    public async Task AskToJoin_ExpiredInvitation_AllowsNewInvite()
    {
        var manager = CreateManager();
        var firstOwner = CreateCharacter(1, "FirstOwner");
        var secondOwner = CreateCharacter(2, "SecondOwner");
        var target = CreateCharacter(3, "Target");
        var invitations = GetActiveInvitations(manager);

        manager.AskToJoin(firstOwner, target.Name, 0, true, target);
        invitations[target.Id].Time = DateTime.UtcNow.AddMinutes(-2);
        manager.AskToJoin(secondOwner, target.Name, 0, true, target);

        await Assert.That(invitations).Count().IsEqualTo(1);
        await Assert.That(invitations[target.Id].Owner).IsSameReferenceAs(secondOwner);
    }

    [Test]
    public async Task ReplyToJoinTeam_TargetJoinedAnotherTeam_DoesNotJoinOwnerTeam()
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Owner");
        var target = CreateCharacter(2, "Target");
        var ownerTeam = CreateTeam(10, owner);
        var targetTeam = CreateTeam(20, target);
        var activeTeams = GetActiveTeams(manager);
        activeTeams[ownerTeam.Id] = ownerTeam;

        manager.AskToJoin(owner, target.Name, ownerTeam.Id, true, target);
        activeTeams[targetTeam.Id] = targetTeam;
        manager.ReplyToJoinTeam(target, ownerTeam.Id, true, owner.Id, false, target.Name, false);

        await Assert.That(ownerTeam.IsMember(target.Id)).IsFalse();
        await Assert.That(GetActiveInvitations(manager)).IsEmpty();
    }

    [Test]
    [Arguments(true, 1000u, 1001u)]
    [Arguments(false, 1001u, 1000u)]
    public async Task UpdateAtLogin_ValidMembership_ReconcilesPartyOrRaidAchievement(
        bool isParty,
        uint expectedAchievementId,
        uint otherAchievementId)
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.EnrollParty);
        data.AddRecord(101, CharRecordKind.EnrollRaidGroup);
        data.AddAchievement(1000, 1, false);
        data.AddAchievement(1001, 1, false);
        data.AddObjective(1, 1000, 100);
        data.AddObjective(2, 1001, 101);

        var character = CreateCharacter(1, "Member");
        var achievements = new CharacterAchievements(character, data.Build());
        s_achievementsField.SetValue(character, achievements);
        s_inPartyField.SetValue(character, true);

        var team = CreateTeam(10, character);
        team.IsParty = isParty;
        var chatManager = Mock.Of<IChatManager>();
        chatManager.GetPartyChat(team, character).Returns(new ChatChannel());
        chatManager.GetRaidChat(team).Returns(new ChatChannel());
        var manager = new TeamManager(
            Mock.Of<IWorldManager>().Object,
            chatManager.Object,
            Mock.Of<ITeamIdManager>().Object);
        GetActiveTeams(manager)[team.Id] = team;

        manager.UpdateAtLogin(character);

        await Assert.That(achievements.GetAmount(expectedAchievementId)).IsEqualTo(1u);
        await Assert.That(achievements.GetAmount(otherAchievementId)).IsEqualTo(0u);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AskRiskyTeam_NonOwnerDismiss_DoesNotChangeTeamOrChat(bool member)
    {
        var world = Mock.Of<IWorldManager>();
        var chat = Mock.Of<IChatManager>();
        var manager = new TeamManager(world.Object, chat.Object, Mock.Of<ITeamIdManager>().Object);
        var owner = CreateCharacter(1, "Owner");
        var actor = CreateCharacter(2, "Actor");
        var team = CreateTeam(10, owner);
        if (member) team.AddMember(actor);
        GetActiveTeams(manager)[team.Id] = team;

        manager.AskRiskyTeam(actor, team.Id, owner.Id, RiskyAction.Dismiss);

        await Assert.That(manager.GetActiveTeam(team.Id)).IsSameReferenceAs(team);
        await Assert.That(team.IsMember(owner.Id)).IsTrue();
        Mock.VerifyNoOtherCalls(chat);
        Mock.VerifyNoOtherCalls(world);
    }

    [Test]
    public async Task AskRiskyTeam_OwnerDismiss_RemovesTeamAndMemberChat()
    {
        var chat = Mock.Of<IChatManager>();
        var manager = new TeamManager(Mock.Of<IWorldManager>().Object, chat.Object, Mock.Of<ITeamIdManager>().Object);
        var owner = CreateCharacter(1, "Owner");
        var team = CreateTeam(10, owner);
        chat.GetPartyChat(team, owner).Returns(new ChatChannel());
        GetActiveTeams(manager)[team.Id] = team;

        manager.AskRiskyTeam(owner, team.Id, owner.Id, RiskyAction.Dismiss);

        await Assert.That(manager.GetActiveTeam(team.Id)).IsNull();
    }

    [Test]
    [Arguments(RiskyAction.Leave, 1u)]
    [Arguments(RiskyAction.Kick, 999u)]
    [Arguments((RiskyAction)99, 1u)]
    public async Task AskRiskyTeam_InvalidActionOrTarget_DoesNotTouchChat(RiskyAction action, uint targetId)
    {
        var chat = Mock.Of<IChatManager>();
        var manager = new TeamManager(Mock.Of<IWorldManager>().Object, chat.Object, Mock.Of<ITeamIdManager>().Object);
        var owner = CreateCharacter(1, "Owner");
        var actor = action == RiskyAction.Leave ? CreateCharacter(2, "Member") : owner;
        var team = CreateTeam(10, owner);
        if (actor != owner) team.AddMember(actor);
        GetActiveTeams(manager)[team.Id] = team;

        manager.AskRiskyTeam(actor, team.Id, targetId, action);

        await Assert.That(manager.GetActiveTeam(team.Id)).IsSameReferenceAs(team);
        Mock.VerifyNoOtherCalls(chat);
    }

    [Test]
    [Arguments(true, false, false, false)]
    [Arguments(false, false, false, false)]
    [Arguments(true, true, false, true)]
    [Arguments(false, true, false, false)]
    [Arguments(false, true, true, true)]
    public async Task SetOverHeadMarker_ChecksMembershipAndRaidOwner(bool party, bool member, bool owner, bool allowed)
    {
        var manager = CreateManager();
        var leader = CreateCharacter(1, "Owner");
        var actor = owner ? leader : CreateCharacter(2, "Actor");
        var team = CreateTeam(10, leader);
        team.IsParty = party;
        if (member && actor != leader) team.AddMember(actor);
        GetActiveTeams(manager)[team.Id] = team;
        // An overhead icon does not confer a rank in the r208022 client.
        team.MarksList[1] = (1, actor.Id);

        manager.SetOverHeadMarker(actor, team.Id, OverHeadMark.Number1, 2, 700);

        await Assert.That(team.MarksList[0].Item2).IsEqualTo(allowed ? 700u : 0u);
    }

    [Test]
    public async Task ChangeLootingRule_OutsideMaster_DoesNotPartiallyChangeRule()
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Owner");
        var team = CreateTeam(10, owner);
        GetActiveTeams(manager)[team.Id] = team;
        var oldGrade = team.LootingRule.MinimumGrade;
        var oldBind = team.LootingRule.RollForBindOnPickup;
        var oldMethod = team.LootingRule.LootMethod;

        manager.ChangeLootingRule(owner, team.Id, 15, LootingRuleMethod.LootMaster, 7, 999, !oldBind);

        await Assert.That(team.LootingRule.LootMaster).IsEqualTo(0u);
        await Assert.That(team.LootingRule.LootMethod).IsEqualTo(oldMethod);
        await Assert.That(team.LootingRule.MinimumGrade).IsEqualTo(oldGrade);
        await Assert.That(team.LootingRule.RollForBindOnPickup).IsEqualTo(oldBind);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ChangeLootingRule_ValidMasterOrMethodOnly_UsesMember(bool explicitMaster)
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Owner");
        var member = CreateCharacter(2, "Member");
        var team = CreateTeam(10, owner);
        team.AddMember(member);
        GetActiveTeams(manager)[team.Id] = team;

        manager.ChangeLootingRule(owner, team.Id, explicitMaster ? (byte)5 : (byte)1,
            LootingRuleMethod.LootMaster, 2, explicitMaster ? member.Id : 0, false);

        await Assert.That(team.LootingRule.LootMethod).IsEqualTo(LootingRuleMethod.LootMaster);
        await Assert.That(team.LootingRule.LootMaster).IsEqualTo(explicitMaster ? member.Id : owner.Id);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task AskRiskyTeam_ValidKick_RemovesTargetChatAndKeepsLeader(bool concurrentKick)
    {
        using var friends = new EmptyFriendsScope();
        var world = Mock.Of<IWorldManager>();
        var chat = Mock.Of<IChatManager>();
        var manager = new TeamManager(world.Object, chat.Object, Mock.Of<ITeamIdManager>().Object);
        var owner = CreateCharacter(1, "Owner");
        var target = CreateCharacter(2, "Target");
        var other = CreateCharacter(3, "Other");
        var members = new[] { owner, target, other };
        var team = CreateTeam(10, owner);
        team.AddMember(target);
        team.AddMember(other);
        var partyChat = new ChatChannel();
        foreach (var character in members)
        {
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(character, true);
            s_inPartyField.SetValue(character, true);
            partyChat.JoinChannel(character);
            chat.GetPartyChat(team, character).Returns(partyChat);
            world.GetCharacterById(character.Id).Returns(character);
        }
        GetActiveTeams(manager)[team.Id] = team;

        if (concurrentKick)
            await Task.WhenAll(
                Task.Run(() => manager.AskRiskyTeam(owner, team.Id, target.Id, RiskyAction.Kick)),
                Task.Run(() => manager.AskRiskyTeam(owner, team.Id, target.Id, RiskyAction.Kick)));
        else
            manager.AskRiskyTeam(owner, team.Id, target.Id, RiskyAction.Kick);

        await Assert.That(manager.GetActiveTeam(team.Id)).IsSameReferenceAs(team);
        await Assert.That(team.MembersCount()).IsEqualTo(2);
        await Assert.That(team.IsMember(target.Id)).IsFalse();
        await Assert.That(partyChat.Members.Contains(target)).IsFalse();
        await Assert.That(partyChat.Members.Contains(owner)).IsTrue();
        await Assert.That(partyChat.Members.Contains(other)).IsTrue();
        await Assert.That(target.InParty).IsFalse();
        await Assert.That(owner.InParty).IsTrue();
        chat.GetPartyChat(team, target).WasCalled(Times.Once);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SetPingPos_MarkedCharacter_MustBelongToTheTeam(bool isMember)
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Owner");
        var actor = CreateCharacter(2, "Actor");
        var team = CreateTeam(10, owner);
        if (isMember)
            team.AddMember(actor);
        team.MarksList[0] = (1, actor.Id);
        GetActiveTeams(manager)[team.Id] = team;
        var previousPosition = team.PingPosition;
        var requestedPosition = new WorldSpawnPosition { X = 123, Y = 456, Z = 78 };

        manager.SetPingPos(actor, team.Id, true, requestedPosition, 1);

        await Assert.That(team.PingPosition).IsSameReferenceAs(isMember ? requestedPosition : previousPosition);
    }

    private sealed class EmptyFriendsScope : IDisposable
    {
        private static readonly FieldInfo s_instance = typeof(Singleton<FriendMananger>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _previous = s_instance.GetValue(null);

        public EmptyFriendsScope()
        {
            var manager = new FriendMananger();
            typeof(FriendMananger).GetField("_allFriends", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, new Dictionary<uint, FriendTemplate>());
            s_instance.SetValue(null, manager);
        }

        public void Dispose() => s_instance.SetValue(null, _previous);
    }

    private static TeamManager CreateManager()
    {
        return new TeamManager(
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IChatManager>().Object,
            Mock.Of<ITeamIdManager>().Object);
    }

    private static CharacterMock CreateCharacter(uint id, string name)
    {
        return new CharacterMock
        {
            Id = id,
            Name = name
        };
    }

    private static Team CreateTeam(uint id, CharacterMock member)
    {
        var team = new Team
        {
            Id = id,
            OwnerId = member.Id,
            IsParty = true
        };
        team.AddMember(member);
        return team;
    }

    private static ConcurrentDictionary<uint, Team> GetActiveTeams(TeamManager manager)
    {
        return (ConcurrentDictionary<uint, Team>)typeof(TeamManager)
            .GetField("_activeTeams", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
    }

    private static ConcurrentDictionary<uint, InvitationTemplate> GetActiveInvitations(TeamManager manager)
    {
        return (ConcurrentDictionary<uint, InvitationTemplate>)typeof(TeamManager)
            .GetField("_activeInvitations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
    }
}
