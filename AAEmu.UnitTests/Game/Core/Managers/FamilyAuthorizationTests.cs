using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.UnitTests.Utils.Mocks;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class FamilyAuthorizationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplyToInvite_UninvitedReply_DoesNotCreateOrJoinFamily(bool hasFamily)
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        if (hasFamily)
            graph.Family(10, inviter, graph.Character(3));

        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Forged");

        await Assert.That(invited.Family).IsEqualTo(0u);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplyToInvite_MatchingInvitation_UsesStoredTitleAndConsumesOnce(bool hasFamily)
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        if (hasFamily)
            graph.Family(10, inviter, graph.Character(3));
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");

        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Forged steward");
        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Replay");

        var family = graph.Manager.GetFamily(invited.Family);
        await Assert.That(family).IsNotNull();
        await Assert.That(family.GetMember(invited).Title).IsEqualTo("Cousin");
        await Assert.That(family.GetMember(invited).Role).IsEqualTo((byte)0);
        await Assert.That(graph.Saves.Count).IsEqualTo(1);
        await Assert.That(graph.Saves[0].GetMember(invited).Title).IsEqualTo("Cousin");
    }

    [Test]
    public async Task ReplyToInvite_WrongInviter_DoesNotConsumeTheValidInvitation()
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");

        graph.Manager.ReplyToInvite(99, invited, true, "Forged");
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Ignored");
        await Assert.That(graph.Saves.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ReplyToInvite_RejectedInvitation_CannotBeReplayed()
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");

        graph.Manager.ReplyToInvite(inviter.Id, invited, false, "Ignored");
        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Replay");

        await Assert.That(invited.Family).IsEqualTo(0u);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(59, true)]
    [Arguments(60, false)]
    public async Task ReplyToInvite_ExpiryBoundary_OnlyUnexpiredInvitationCanJoin(int seconds, bool expectedJoin)
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");
        graph.Now = graph.Now.AddSeconds(seconds);

        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Ignored");

        await Assert.That(invited.Family != 0).IsEqualTo(expectedJoin);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReplyToInvite_ReplacedSession_RejectsOldInvitation(bool replaceInviter)
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");
        var replaced = replaceInviter ? inviter : invited;
        replaced.Connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = replaced };

        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Ignored");

        await Assert.That(invited.Family).IsEqualTo(0u);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReplyToInvite_BlockedAfterInvitation_RejectsJoin()
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");
        invited.Blocked.BlockedList.Add(inviter.Id, new BlockedTemplate { Owner = invited.Id, BlockedId = inviter.Id });

        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Ignored");

        await Assert.That(invited.Family).IsEqualTo(0u);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplyToInvite_InviterChangesFamilyOrSteward_RejectsJoin(bool changeSteward)
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var member = graph.Character(3);
        var invited = graph.Character(2);
        var family = graph.Family(10, inviter, member);
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");
        if (changeSteward)
            family.GetMember(inviter).Role = 0;
        else
            inviter.Family = 20;

        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Ignored");

        await Assert.That(invited.Family).IsEqualTo(0u);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InviteToFamily_DuplicateInvitation_DoesNotReplaceTheChosenTitle()
    {
        using var graph = new FamilyGraph();
        var inviter = graph.Character(1);
        var invited = graph.Character(2);
        graph.Manager.InviteToFamily(inviter, invited.Name, "Cousin");
        graph.Manager.InviteToFamily(inviter, invited.Name, "Changed");
        graph.Manager.ReplyToInvite(inviter.Id, invited, true, "Ignored");

        await Assert.That(graph.Manager.GetFamily(invited.Family).GetMember(invited).Title).IsEqualTo("Cousin");
    }

    [Test]
    [Arguments(7, true)]
    [Arguments(8, false)]
    public async Task InviteToFamily_MemberBoundary_EnforcesEightMembers(int count, bool expectedJoin)
    {
        using var graph = new FamilyGraph();
        var members = Enumerable.Range(1, count).Select(id => graph.Character((uint)id)).ToArray();
        var family = graph.Family(10, members);
        var invited = graph.Character(20);
        graph.Manager.InviteToFamily(members[0], invited.Name, "Cousin");
        graph.Manager.ReplyToInvite(members[0].Id, invited, true, "Ignored");

        await Assert.That(invited.Family != 0).IsEqualTo(expectedJoin);
        await Assert.That(family.Members.Count).IsEqualTo(8);
    }

    [Test]
    public async Task ReplyToInvite_ConcurrentLastSlot_AdmitsOnlyOneMember()
    {
        using var graph = new FamilyGraph();
        var members = Enumerable.Range(1, 7).Select(id => graph.Character((uint)id)).ToArray();
        var family = graph.Family(10, members);
        var invitedA = graph.Character(20);
        var invitedB = graph.Character(21);
        graph.Manager.InviteToFamily(members[0], invitedA.Name, "A");
        graph.Manager.InviteToFamily(members[0], invitedB.Name, "B");

        await Task.WhenAll(
            Task.Run(() => graph.Manager.ReplyToInvite(members[0].Id, invitedA, true, "Ignored")),
            Task.Run(() => graph.Manager.ReplyToInvite(members[0].Id, invitedB, true, "Ignored")));

        await Assert.That(family.Members.Count).IsEqualTo(8);
        await Assert.That(new[] { invitedA, invitedB }.Count(character => character.Family == 10)).IsEqualTo(1);
        await Assert.That(graph.Saves.Count).IsEqualTo(1);
    }

    [Test]
    public async Task InviteToFamily_NonSteward_CannotInvite()
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        var invited = graph.Character(3);
        graph.Family(10, owner, member);

        graph.Manager.InviteToFamily(member, invited.Name, "Cousin");
        graph.Manager.ReplyToInvite(member.Id, invited, true, "Ignored");

        await Assert.That(invited.Family).IsEqualTo(0u);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(3u)]
    [Arguments(99u)]
    public async Task FamilyActions_OutsideOrUnknownMember_DoNotChangeEitherFamily(uint targetId)
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        var otherOwner = graph.Character(3);
        var otherMember = graph.Character(4);
        var family = graph.Family(10, owner, member);
        var otherFamily = graph.Family(20, otherOwner, otherMember);

        graph.Manager.ChangeTitle(owner, targetId, "Wrong family");
        graph.Manager.ChangeOwner(owner, targetId);
        graph.Manager.KickMember(owner, targetId);

        await Assert.That(family.Members.Count).IsEqualTo(2);
        await Assert.That(otherFamily.Members.Count).IsEqualTo(2);
        await Assert.That(otherFamily.GetMember(otherOwner).Title).IsEqualTo("");
        await Assert.That(family.GetMember(owner).Role).IsEqualTo((byte)1);
        await Assert.That(otherFamily.GetMember(otherOwner).Role).IsEqualTo((byte)1);
        await Assert.That(otherOwner.Family).IsEqualTo(20u);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FamilyActions_NonStewardAndSelfTransfer_DoNotChangeOwner()
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        var family = graph.Family(10, owner, member);

        graph.Manager.ChangeTitle(member, owner.Id, "Forged");
        graph.Manager.ChangeOwner(member, member.Id);
        graph.Manager.KickMember(member, owner.Id);
        graph.Manager.ChangeOwner(owner, owner.Id);
        graph.Manager.KickMember(owner, owner.Id);

        await Assert.That(family.GetMember(owner).Role).IsEqualTo((byte)1);
        await Assert.That(family.GetMember(owner).Title).IsEqualTo("");
        await Assert.That(family.Members.Count).IsEqualTo(2);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FamilyChanges_PersistenceFails_PreserveMembershipTitleAndOwner(bool joining)
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        var invited = graph.Character(3);
        var family = graph.Family(10, owner, member);
        graph.Manager.PersistFamily = _ => throw new InvalidOperationException("Injected family save failure");
        if (joining)
        {
            graph.Manager.InviteToFamily(owner, invited.Name, "Cousin");
            graph.Manager.ReplyToInvite(owner.Id, invited, true, "Ignored");
        }
        else
        {
            graph.Manager.ChangeTitle(owner, member.Id, "Changed");
            graph.Manager.ChangeOwner(owner, member.Id);
            graph.Manager.KickMember(owner, member.Id);
        }

        await Assert.That(invited.Family).IsEqualTo(0u);
        await Assert.That(family.Members.Count).IsEqualTo(2);
        await Assert.That(family.GetMember(member).Title).IsEqualTo("");
        await Assert.That(family.GetMember(member).Role).IsEqualTo((byte)0);
        await Assert.That(family.GetMember(owner).Role).IsEqualTo((byte)1);
    }

    [Test]
    public async Task ChangeTitleAndOwner_PersistTheExactChangedMembers()
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        var family = graph.Family(10, owner, member);

        graph.Manager.ChangeTitle(owner, member.Id, "Cousin");
        graph.Manager.ChangeOwner(owner, member.Id);

        await Assert.That(graph.Saves.Count).IsEqualTo(2);
        await Assert.That(graph.Saves[0].GetMember(member).Title).IsEqualTo("Cousin");
        await Assert.That(graph.Saves[1].GetMember(member).Title).IsEqualTo("Cousin");
        await Assert.That(graph.Saves[1].GetMember(member).Role).IsEqualTo((byte)1);
        await Assert.That(graph.Saves[1].GetMember(owner).Role).IsEqualTo((byte)0);
        await Assert.That(family.Members.Count(member => member.Role == 1)).IsEqualTo(1);
    }

    [Test]
    public async Task LeaveFamily_StewardMustTransferBeforeLeaving()
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var successor = graph.Character(2);
        var family = graph.Family(10, owner, successor, graph.Character(3));

        graph.Manager.LeaveFamily(owner);

        await Assert.That(family.Members.Count).IsEqualTo(3);
        await Assert.That(graph.Saves.Count).IsEqualTo(0);
        graph.Manager.ChangeOwner(owner, successor.Id);
        graph.Manager.LeaveFamily(owner);
        await Assert.That(family.Members.Count).IsEqualTo(2);
        await Assert.That(family.GetMember(successor).Role).IsEqualTo((byte)1);
        await Assert.That(owner.Family).IsEqualTo(0u);
    }

    [Test]
    public async Task RemoveDeletedCharacter_Steward_DisbandDoesNotLeaveAnOwnerlessFamily()
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var members = new[] { owner, graph.Character(2), graph.Character(3) };
        graph.Family(10, members);

        graph.Manager.RemoveDeletedCharacter(owner);

        await Assert.That(graph.Manager.GetFamily(10)).IsNull();
        await Assert.That(members.All(character => character.Family == 0)).IsTrue();
        await Assert.That(graph.Saves.Count).IsEqualTo(1);
        await Assert.That(graph.Saves[0].Members.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(8)]
    [Arguments(9)]
    public async Task FamilyDescription_NativeMemberLimit_WritesTheCompleteBoundedBody(int memberCount)
    {
        var family = new Family { Id = 10 };
        for (var index = 0; index < memberCount; index++)
            family.AddMember(new FamilyMember
            {
                Id = (uint)index + 1, Name = $"Member{index}", Role = (byte)(index == 0 ? 1 : 0), Title = $"Title{index}"
            });
        var stream = new PacketStream();
        family.Write(stream);
        var body = new PacketStream(stream.GetBytes());

        await Assert.That(body.ReadUInt32()).IsEqualTo(10u);
        var count = Math.Min(memberCount, Family.MaximumMembers);
        await Assert.That(body.ReadInt32()).IsEqualTo(count);
        for (var index = 0; index < count; index++)
        {
            await Assert.That(body.ReadUInt32()).IsEqualTo((uint)index + 1);
            await Assert.That(body.ReadString()).IsEqualTo($"Member{index}");
            await Assert.That(body.ReadByte()).IsEqualTo((byte)(index == 0 ? 1 : 0));
            await Assert.That(body.ReadBoolean()).IsFalse();
            await Assert.That(body.ReadString()).IsEqualTo($"Title{index}");
        }
        await Assert.That(body.Pos).IsEqualTo(body.Count);
    }

    [Test]
    public async Task ChangeTitle_ConcurrentCharacterSave_WaitsUntilLiveStateMatchesTheCommit()
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        var family = graph.Family(10, owner, member);
        var saveEnteredDuringFamilyWrite = true;
        var writerThreadId = 0;
        var saveThreadId = 0;
        var saveThreadCompleted = false;
        graph.Manager.PersistFamily = _ =>
        {
            writerThreadId = Environment.CurrentManagedThreadId;
            var saveAttempt = new Thread(() =>
            {
                saveThreadId = Environment.CurrentManagedThreadId;
                saveEnteredDuringFamilyWrite = Monitor.TryEnter(SaveManager.PersistenceSyncRoot);
                if (saveEnteredDuringFamilyWrite)
                    Monitor.Exit(SaveManager.PersistenceSyncRoot);
            }) { IsBackground = true };
            saveAttempt.Start();
            saveThreadCompleted = saveAttempt.Join(TimeSpan.FromSeconds(5));
        };

        graph.Manager.ChangeTitle(owner, member.Id, "Cousin");

        await Assert.That(saveThreadCompleted).IsTrue();
        await Assert.That(saveThreadId).IsNotEqualTo(writerThreadId);
        await Assert.That(saveEnteredDuringFamilyWrite).IsFalse();
        await Assert.That(family.GetMember(member).Title).IsEqualTo("Cousin");
        var savedTitle = await Task.Run(() =>
        {
            lock (SaveManager.PersistenceSyncRoot)
                return family.GetMember(member).Title;
        });
        await Assert.That(savedTitle).IsEqualTo("Cousin");
    }

    [Test]
    [Arguments(false, 45, true)]
    [Arguments(false, 46, false)]
    [Arguments(true, 34, true)]
    [Arguments(true, 35, false)]
    public async Task FamilyTitle_StorageAndNativeByteBounds_ApplyAtInviteAndTitleChange(bool multibyte, int length, bool valid)
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        var invited = graph.Character(3);
        var family = graph.Family(10, owner, member);
        var title = new string(multibyte ? '界' : 'A', length);
        if (multibyte && length == 34)
            title += "AA"; // Exactly 104 UTF-8 bytes.
        graph.Manager.ChangeTitle(owner, member.Id, title);
        graph.Manager.InviteToFamily(owner, invited.Name, title);
        graph.Manager.ReplyToInvite(owner.Id, invited, true, "Ignored");

        await Assert.That(family.GetMember(member).Title).IsEqualTo(valid ? title : "");
        await Assert.That(invited.Family != 0).IsEqualTo(valid);
        await Assert.That(graph.Saves.Count).IsEqualTo(valid ? 2 : 0);
    }

    private sealed class FamilyGraph : IDisposable
    {
        private readonly AchievementDataBuilder _achievementData = new();
        private readonly Mock<IWorldManager> _world = Mock.Of<IWorldManager>();
        private readonly Dictionary<uint, Family> _families = [];
        private readonly Dictionary<uint, FamilyMember> _members = [];
        public FamilyManager Manager { get; }
        public List<Family> Saves { get; } = [];
        public DateTime Now { get; set; } = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

        public FamilyGraph()
        {
            var ids = Mock.Of<IFamilyIdManager>();
            ids.GetNextId().Returns(100u);
            Manager = new FamilyManager(_world.Object, Mock.Of<IChatManager>().Object, ids.Object)
            {
                InvitationTime = () => Now,
                PersistFamily = family => Saves.Add(family)
            };
            typeof(FamilyManager).GetField("_families", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Manager, _families);
            typeof(FamilyManager).GetField("_familyMembers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Manager, _members);
        }

        public CharacterMock Character(uint id)
        {
            var character = new CharacterMock
            {
                Id = id,
                Name = $"Member{id}",
                Connection = new GameConnection(Mock.Of<ISession>().Object)
            };
            character.Connection.ActiveChar = character;
            character.Blocked = new CharacterBlocked(character);
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(character, true);
            typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(character, new CharacterAchievements(character, _achievementData.Build()));
            _world.GetCharacter(character.Name).Returns(character);
            return character;
        }

        public Family Family(uint id, params Character[] characters)
        {
            var family = new Family { Id = id };
            foreach (var character in characters)
            {
                var member = new FamilyMember
                {
                    Id = character.Id, Name = character.Name, Title = "", Character = character,
                    Role = (byte)(family.Members.Count == 0 ? 1 : 0)
                };
                character.Family = id;
                family.AddMember(member);
                _members.Add(member.Id, member);
            }
            _families.Add(id, family);
            return family;
        }

        public void Dispose() => _achievementData.Dispose();
    }
}
