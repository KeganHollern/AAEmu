using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class FamilyAuthorizationTests
{
    [Test]
    public async Task WorldTitles_FollowTheSavedMembersAndStewardUntilDisband()
    {
        using var graph = new FamilyGraph();
        var founder = graph.Character(1);
        var second = graph.Character(2);
        var third = graph.Character(3);
        graph.Manager.InviteToFamily(founder, second.Name, "Cousin");
        graph.Manager.ReplyToInvite(founder.Id, second, true, "Cousin");
        await Assert.That(founder.Labels.Single()).IsEqualTo((founder.ObjId, (byte)1, founder.Name, "", true));
        await Assert.That(second.Labels.Single()).IsEqualTo((second.ObjId, (byte)0, founder.Name, "Cousin", true));

        graph.Manager.InviteToFamily(founder, third.Name, "Brother");
        graph.Manager.ReplyToInvite(founder.Id, third, true, "Brother");
        await Assert.That(third.Labels.Single()).IsEqualTo((third.ObjId, (byte)0, founder.Name, "Brother", true));
        graph.Manager.ChangeTitle(founder, third.Id, "Sister");
        await Assert.That(third.Labels.Last()).IsEqualTo((third.ObjId, (byte)0, founder.Name, "Sister", true));

        graph.Manager.ChangeOwner(founder, second.Id);
        await Assert.That(founder.Labels.Last()).IsEqualTo((founder.ObjId, (byte)0, second.Name, "", true));
        await Assert.That(second.Labels.Last()).IsEqualTo((second.ObjId, (byte)1, second.Name, "Cousin", true));
        await Assert.That(third.Labels.Last()).IsEqualTo((third.ObjId, (byte)0, second.Name, "Sister", true));

        graph.Manager.KickMember(second, third.Id);
        await Assert.That(third.Labels.Last()).IsEqualTo((third.ObjId, (byte)0, "", "", true));
        graph.Manager.LeaveFamily(founder);
        await Assert.That(founder.Labels.Last()).IsEqualTo((founder.ObjId, (byte)0, "", "", true));
        await Assert.That(second.Labels.Last()).IsEqualTo((second.ObjId, (byte)0, "", "", true));
    }

    [Test]
    public async Task WorldTitles_FailedSaveDoesNotAnnounceAChangeToNearbyPlayers()
    {
        using var graph = new FamilyGraph();
        var owner = graph.Character(1);
        var member = graph.Character(2);
        graph.Family(10, owner, member);
        graph.Manager.PersistFamily = _ => throw new InvalidOperationException("Test save failure");

        graph.Manager.ChangeTitle(owner, member.Id, "Cousin");
        graph.Manager.ChangeOwner(owner, member.Id);
        graph.Manager.LeaveFamily(member);

        await Assert.That(owner.Labels).IsEmpty();
        await Assert.That(member.Labels).IsEmpty();
    }

    private sealed class FamilyTestCharacter : CharacterMock
    {
        public List<(uint ObjectId, byte Role, string Steward, string Title, bool Self)> Labels { get; } = [];

        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            if (packet is SCFamilyTitlePacket title)
            {
                var stream = new PacketStream(title.Write(new PacketStream()).GetBytes());
                Labels.Add((stream.ReadBc(), stream.ReadByte(), stream.ReadString(), stream.ReadString(), self));
            }
        }
    }
}
