using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects;

public class InteractionEffectTests
{
    private const uint MirageIsleExitSkillId = 17838;

    [Test]
    public async Task ExecuteWorldInteraction_ExitIndun_SendsSkillEndBeforeInteraction()
    {
        const ushort skillTlId = 0x1234;
        var events = new List<string>();
        var character = new RecordingCharacter(events);
        var interaction = new RecordingInteraction(events);
        var skill = new Skill(new SkillTemplate { Id = MirageIsleExitSkillId })
        {
            TlId = skillTlId
        };

        InteractionEffect.ExecuteWorldInteraction(
            interaction,
            character,
            new SkillCasterUnit(),
            new Doodad(),
            new SkillCastUnitTarget(),
            new EffectSource(skill),
            0,
            (_, skillId) => new DoodadFunc
            {
                FuncType = nameof(DoodadFuncExitIndun),
                SkillId = skillId
            });

        await Assert.That(events.SequenceEqual(["skill-ended", "interaction"])).IsTrue();
        await Assert.That(character.Packets.Count).IsEqualTo(1);

        var packet = character.Packets[0];
        await Assert.That(packet).IsTypeOf<SCSkillEndedPacket>();
        await Assert.That(packet.TypeId).IsEqualTo(SCOffsets.SCSkillEndedPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.Count).IsEqualTo(2);
        await Assert.That(stream.ReadUInt16()).IsEqualTo(skillTlId);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task ExecuteWorldInteraction_OtherDoodadFunc_DoesNotSendEarlySkillEnd()
    {
        var events = new List<string>();
        var character = new RecordingCharacter(events);
        var interaction = new RecordingInteraction(events);
        var skill = new Skill(new SkillTemplate { Id = MirageIsleExitSkillId }) { TlId = 0x1234 };

        InteractionEffect.ExecuteWorldInteraction(
            interaction,
            character,
            new SkillCasterUnit(),
            new Doodad(),
            new SkillCastUnitTarget(),
            new EffectSource(skill),
            0,
            (_, skillId) => new DoodadFunc
            {
                FuncType = nameof(DoodadFuncUse),
                SkillId = skillId
            });

        await Assert.That(events.SequenceEqual(["interaction"])).IsTrue();
        await Assert.That(character.Packets).IsEmpty();
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

    private sealed class RecordingInteraction(List<string> events) : IWorldInteraction
    {
        public void Execute(BaseUnit caster, SkillCaster casterType, BaseUnit target,
            SkillCastTarget targetType, uint skillId, uint doodadId = 0, DoodadFuncTemplate objectFunc = null)
        {
            events.Add("interaction");
        }
    }
}
