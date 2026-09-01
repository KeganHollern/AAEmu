using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

public class ExitArchemallTests
{
    [Test]
    public async Task ExitInstance_SendsSkillEndBeforeLeaveRequest()
    {
        var events = new List<string>();
        var character = new RecordingCharacter(events);
        var skill = new Skill(new SkillTemplate { Id = 26152 }) { TlId = 0x1234 };

        ExitArchemall.ExitInstance(character, skill, () => events.Add("leave-instance"));

        await Assert.That(events.SequenceEqual(["skill-ended", "leave-instance"])).IsTrue();
        await Assert.That(character.Packets.Count).IsEqualTo(1);

        var packet = character.Packets[0];
        await Assert.That(packet).IsTypeOf<SCSkillEndedPacket>();
        await Assert.That(packet.TypeId).IsEqualTo(SCOffsets.SCSkillEndedPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.Count).IsEqualTo(2);
        await Assert.That(stream.ReadUInt16()).IsEqualTo((ushort)0x1234);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    private sealed class RecordingCharacter(List<string> events) : Character(null)
    {
        public List<GamePacket> Packets { get; } = [];

        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            Packets.Add(packet);
            events.Add("skill-ended");
        }
    }
}
