using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCDoodadPhaseChangedPacketTests
{
    [Test]
    public async Task Write_PreservesThePhaseSnapshotAfterTheDoodadChanges()
    {
        var doodad = new Doodad { ObjId = 1234, ItemTemplateId = 5678, GrowthTime = DateTime.UtcNow.AddMinutes(3) };
        SetPhase(doodad, 3150);
        var packet = new SCDoodadPhaseChangedPacket(doodad);
        var original = packet.Write(new PacketStream());
        original.Rollback();
        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x10f);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(original.ReadBc()).IsEqualTo(1234u);
        await Assert.That(original.ReadUInt32()).IsEqualTo(3150u);
        var timeLeft = original.ReadUInt32();
        await Assert.That(timeLeft).IsGreaterThan(0u).And.IsLessThanOrEqualTo(180000u);
        await Assert.That(original.ReadInt32()).IsEqualTo(-1);
        await Assert.That(original.ReadUInt32()).IsEqualTo(5678u);
        await Assert.That(original.LeftBytes).IsEqualTo(0);

        doodad.ObjId = 4321;
        SetPhase(doodad, 17070);
        doodad.GrowthTime = DateTime.MinValue;
        doodad.ItemTemplateId = 8765;
        var later = packet.Write(new PacketStream());
        later.Rollback();
        await Assert.That(later.ReadBc()).IsEqualTo(1234u);
        await Assert.That(later.ReadUInt32()).IsEqualTo(3150u);
        await Assert.That(later.ReadUInt32()).IsEqualTo(timeLeft);
        await Assert.That(later.ReadInt32()).IsEqualTo(-1);
        await Assert.That(later.ReadUInt32()).IsEqualTo(5678u);
        await Assert.That(later.LeftBytes).IsEqualTo(0);
    }

    private static void SetPhase(Doodad doodad, uint phase) => typeof(Doodad)
        .GetField("_funcGroupId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(doodad, phase);
}
