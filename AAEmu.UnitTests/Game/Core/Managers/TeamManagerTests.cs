using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Team;
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
