using System.Reflection;

using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Models.Game.Units;

using TUnit.Mocks.Arguments;

namespace AAEmu.UnitTests.Game.Core.Managers;

public partial class TeamManagerTests
{
    [Test]
    [NotInParallel]
    public async Task RadiusInvite_NearbyPlayerAcceptsRaidAndDistantPlayerGetsNoOffer()
    {
        using var friends = new EmptyFriendsScope();
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Leader");
        var near = CreateCharacter(2, "Nearby");
        var far = CreateCharacter(3, "Outside");
        SetNearbyPlayers((owner, 1f), (near, 100.9f), (far, 101.1f));
        var raid = CreateTeam(10, owner);
        raid.IsParty = false;
        GetActiveTeams(manager)[raid.Id] = raid;

        manager.InviteAreaToTeam(owner, raid.Id, false);
        var invitations = GetActiveInvitations(manager);
        await Assert.That(invitations.ContainsKey(near.Id)).IsTrue();
        await Assert.That(invitations.ContainsKey(far.Id)).IsFalse();

        manager.ReplyToJoinTeam(near, raid.Id, false, owner.Id, false, near.Name, true);
        await Assert.That(raid.IsMember(near.Id)).IsTrue();
        await Assert.That(invitations).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task RadiusInvite_SoloLeaderAndTwoAcceptancesCreateOneRaid()
    {
        using var friends = new EmptyFriendsScope();
        var manager = CreateRaidManager();
        var owner = CreateCharacter(1, "Leader");
        var first = CreateCharacter(2, "First");
        var second = CreateCharacter(3, "Second");
        SetNearbyPlayers((owner, 1f), (first, 10f), (second, 11f));

        manager.InviteAreaToTeam(owner, 0, false);
        manager.ReplyToJoinTeam(first, 0, false, owner.Id, false, first.Name, true);
        manager.ReplyToJoinTeam(second, 0, false, owner.Id, false, second.Name, true);

        var raid = manager.GetActiveTeam(10);
        await Assert.That(raid.IsParty).IsFalse();
        await Assert.That(raid.MembersCount()).IsEqualTo(3);
        await Assert.That(raid.OwnerId).IsEqualTo(owner.Id);
        await Assert.That(GetActiveInvitations(manager)).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RadiusInvite_DeclineOrExpiryLeavesNewDirectInvitationAlone(bool expired)
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Leader");
        var player = CreateCharacter(2, "Player");
        SetNearbyPlayers((owner, 1f), (player, 2f));
        var delivery = new TransferVisibilityTests.RecordingSession();
        player.Connection = new GameConnection(delivery);

        manager.InviteAreaToTeam(owner, 0, false);
        await Assert.That(BitConverter.ToUInt16(delivery.Packets.Single(), 6)).IsEqualTo(SCOffsets.SCAskToJoinTeamAreaPacket);
        var invites = GetActiveInvitations(manager);
        if (expired)
            invites[player.Id].Time = DateTime.UtcNow.AddMinutes(-2);
        manager.ReplyToJoinTeam(player, 0, false, owner.Id, !expired, player.Name, true);
        await Assert.That(invites).IsEmpty();
        await Assert.That(GetActiveTeams(manager)).IsEmpty();

        manager.AskToJoin(owner, player.Name, 0, true, player);
        await Assert.That(BitConverter.ToUInt16(delivery.Packets.Last(), 6)).IsEqualTo(SCOffsets.SCAskToJoinTeamPacket);
        manager.ReplyToJoinTeam(player, 0, false, owner.Id, true, player.Name, true);
        await Assert.That(invites.ContainsKey(player.Id)).IsTrue();
        manager.ReplyToJoinTeam(player, 0, true, owner.Id, true, player.Name, false);
        await Assert.That(invites).IsEmpty();
    }

    [Test]
    public async Task RadiusInvite_CooldownReportsRemainingMillisecondsAndThenPermitsAnotherInvite()
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Leader");
        var player = CreateCharacter(2, "Player");
        var delivery = new TransferVisibilityTests.RecordingSession();
        owner.Connection = new GameConnection(delivery);
        SetNearbyPlayers((owner, 1f), (player, 2f));

        manager.InviteAreaToTeam(owner, 0, false);
        var result = delivery.Packets.Single();
        await Assert.That(BitConverter.ToUInt16(result, 6)).IsEqualTo(SCOffsets.SCTeamAreaInvitedPacket);
        await Assert.That(result[12]).IsEqualTo((byte)1);

        manager.ReplyToJoinTeam(player, 0, false, owner.Id, true, player.Name, true);
        manager.InviteAreaToTeam(owner, 0, false);
        result = delivery.Packets.Last();
        await Assert.That(BitConverter.ToUInt16(result, 6)).IsEqualTo(SCOffsets.SCTeamAreaInvitedPacket);
        var remaining = BitConverter.ToUInt32(result, 8);
        await Assert.That(remaining > 0 && remaining <= 30000).IsTrue();
        await Assert.That(result[12]).IsEqualTo((byte)0);
        await Assert.That(GetActiveInvitations(manager)).IsEmpty();

        owner.NextRadiusInvite = DateTime.UtcNow.AddSeconds(-1);
        manager.InviteAreaToTeam(owner, 0, false);
        await Assert.That(GetActiveInvitations(manager).ContainsKey(player.Id)).IsTrue();
    }

    [Test]
    public async Task RadiusInvite_SkipsBlockedHostileAndGroupedRecipients()
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Sender");
        var blocked = CreateCharacter(2, "Blocked");
        var hostile = CreateCharacter(3, "Hostile");
        var grouped = CreateCharacter(4, "Grouped");
        var eligible = CreateCharacter(5, "Eligible");
        SetNearbyPlayers((owner, 1f), (blocked, 2f), (hostile, 3f), (grouped, 4f), (eligible, 5f));
        blocked.Blocked = new CharacterBlocked(blocked);
        blocked.Blocked.BlockedList[owner.Id] = new BlockedTemplate { Owner = blocked.Id, BlockedId = owner.Id };
        owner.Faction = new SystemFaction { Id = FactionsEnum.Nuian };
        hostile.Faction = new SystemFaction { Id = FactionsEnum.Harani };
        owner.Faction.Relations[hostile.Faction.Id] = new FactionRelation { State = RelationState.Hostile };
        GetActiveTeams(manager)[20] = CreateTeam(20, grouped);

        manager.InviteAreaToTeam(owner, 0, false);
        var invitations = GetActiveInvitations(manager);
        await Assert.That(invitations.Count).IsEqualTo(1);
        await Assert.That(invitations.ContainsKey(eligible.Id)).IsTrue();
    }

    [Test]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    public async Task RadiusInvite_IneligibleLeaderCannotInvite(bool party, bool otherLeader, bool full)
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Sender");
        var player = CreateCharacter(2, "Player");
        SetNearbyPlayers((owner, 1f), (player, 2f));
        var raid = CreateTeam(10, owner);
        raid.IsParty = party;
        if (otherLeader)
        {
            var leader = CreateCharacter(3, "RaidLeader");
            raid.AddMember(leader);
            raid.OwnerId = leader.Id;
        }
        if (full)
        {
            for (uint index = 1; index < raid.Members.Length; index++)
                raid.AddMember(CreateCharacter(index + 10, $"Member{index}"));
        }
        GetActiveTeams(manager)[raid.Id] = raid;

        manager.InviteAreaToTeam(owner, raid.Id, false);
        await Assert.That(GetActiveInvitations(manager)).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RadiusInvite_MembershipChangeBeforeAcceptancePreventsJoin(bool recipientJoined)
    {
        var manager = CreateManager();
        var owner = CreateCharacter(1, "Leader");
        var player = CreateCharacter(2, "Player");
        var other = CreateCharacter(3, "Other");
        var raid = CreateTeam(10, owner);
        raid.IsParty = false;
        GetActiveTeams(manager)[raid.Id] = raid;
        SetNearbyPlayers((owner, 1f), (player, 2f));
        manager.InviteAreaToTeam(owner, raid.Id, false);

        if (recipientJoined)
            GetActiveTeams(manager)[20] = CreateTeam(20, player);
        else
        {
            raid.AddMember(other);
            raid.OwnerId = other.Id;
        }
        manager.ReplyToJoinTeam(player, raid.Id, false, owner.Id, false, player.Name, true);
        await Assert.That(raid.IsMember(player.Id)).IsFalse();
        await Assert.That(GetActiveInvitations(manager)).IsEmpty();
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RadiusInvite_LeaveWorldClearsOnlyOffersForTheDepartingPlayer(bool recipientLeaves)
    {
        using var friends = new EmptyFriendsScope();
        var manager = CreateRaidManager();
        var singleton = typeof(Singleton<TeamManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = singleton.GetValue(null);
        singleton.SetValue(null, manager);
        try
        {
            var owner = CreateCharacter(1, "SoloLeader");
            var player = CreateCharacter(2, "Nearby");
            var direct = CreateCharacter(3, "Direct");
            SetNearbyPlayers((owner, 1f), (player, 2f));
            manager.InviteAreaToTeam(owner, 0, false);
            manager.AskToJoin(owner, direct.Name, 0, true, direct);

            // Character selection and connection loss both clear this property.
            (recipientLeaves ? player : owner).IsOnline = false;
            var pending = GetActiveInvitations(manager);
            await Assert.That(pending.ContainsKey(player.Id)).IsFalse();
            manager.ReplyToJoinTeam(player, 0, false, owner.Id, false, player.Name, true);
            await Assert.That(GetActiveTeams(manager)).IsEmpty();
            await Assert.That(pending.ContainsKey(direct.Id)).IsTrue();
        }
        finally
        {
            singleton.SetValue(null, original);
        }
    }

    private static TeamManager CreateRaidManager()
    {
        var chat = Mock.Of<IChatManager>();
        var ids = Mock.Of<ITeamIdManager>();
        ids.GetNextId().Returns(10u);
        chat.GetPartyChat(Arg.Any<Team>(), Arg.Any<Character>()).Returns(new ChatChannel());
        chat.GetRaidChat(Arg.Any<Team>()).Returns(new ChatChannel());
        return new TeamManager(Mock.Of<IWorldManager>().Object, chat.Object, ids.Object);
    }

    private static void SetNearbyPlayers(params (Character character, float x)[] players)
    {
        var region = new Region(null, 0, 0, 0);
        var objects = new GameObject[players.Length];
        for (var index = 0; index < players.Length; index++)
        {
            var (character, x) = players[index];
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(character, true);
            character.Region = region;
            character.ObjId = character.Id;
            character.Transform.Local.SetPosition(x, 1f, 0f);
            objects[index] = character;
        }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Region).GetField("_neighbors", fields)!.SetValue(region, new[] { region });
        typeof(Region).GetField("_objects", fields)!.SetValue(region, objects);
        typeof(Region).GetField("_objectsSize", fields)!.SetValue(region, objects.Length);
    }
}
