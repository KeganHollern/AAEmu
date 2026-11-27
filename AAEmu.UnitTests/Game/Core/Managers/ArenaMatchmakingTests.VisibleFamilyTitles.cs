using TUnit.Assertions.Enums;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class ArenaMatchmakingTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegionCrossing_NearbyPlayerReceivesFamilyChangeDuringFirstVisibility(bool observerMoves)
    {
        var (world, _, subject, observer) = CreateTransferMatch();
        var steward = SpawnParticipant(3, 1003, 8, world);
        var families = new FamilyManager(_worlds, null, null) { PersistFamily = _ => { } };
        var family = new Family { Id = 20 };
        family.AddMember(new FamilyMember { Id = steward.Id, Name = steward.Name, Role = 1, Title = "", Character = steward });
        family.AddMember(new FamilyMember { Id = subject.Id, Name = subject.Name, Role = 0, Title = "Cousin", Character = subject });
        subject.Family = steward.Family = family.Id;
        Set(families, "_families", new Dictionary<uint, Family> { [family.Id] = family });
        Install(families);

        // The live world builds its regions before it caches their neighbors. This small fixture creates them on demand.
        for (var x = 0; x < 4; x++)
            world.Regions[x, 0] ??= new Region(world, x, 0, 0);
        for (var x = 0; x < 4; x++)
            Set(world.Regions[x, 0], "_neighbors", null);

        var sector = WorldManager.REGION_SIZE;
        observer.Transform.Local.SetPosition(3 * sector + 1, 10, 10);
        _worlds.AddVisibleObject(observer);
        await Assert.That(WorldManager.GetAround<Character>(subject)).DoesNotContain(observer);

        // A child becomes visible after its parent's state, points, and public title. Put the concurrent title change there.
        var changed = false;
        var child = new FamilyVisibilityChild(character =>
        {
            if (character != observer || changed)
                return;
            changed = true;
            families.ChangeTitle(steward, subject.Id, "Sister");
        });
        child.Transform.Parent = subject.Transform;
        var packets = _transferSessions[observer.ObjId].Packets;
        packets.Clear();
        var mover = observerMoves ? observer : subject;
        mover.Transform.Local.SetPosition((observerMoves ? 2 : 1) * sector + 1, 10, 10);
        _worlds.AddVisibleObject(mover);

        var titles = new List<string>();
        foreach (var packet in packets.Where(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCFamilyTitlePacket))
        {
            var body = new PacketStream(packet[8..]);
            if (body.ReadBc() != subject.ObjId)
                continue;
            body.ReadByte();
            body.ReadString();
            titles.Add(body.ReadString());
        }
        await Assert.That(changed).IsTrue();
        await Assert.That(family.GetMember(subject).Title).IsEqualTo("Sister");
        await Assert.That(WorldManager.GetAround<Character>(subject)).Contains(observer);
        await Assert.That(titles).IsEquivalentTo(["Cousin", "Sister"], CollectionOrdering.Matching);
    }

    private sealed class FamilyVisibilityChild(Action<Character> onVisible) : GameObject
    {
        public override void AddVisibleObject(Character character) => onVisible(character);
    }
}
