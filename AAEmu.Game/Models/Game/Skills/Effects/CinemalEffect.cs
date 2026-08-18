using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class CinemalEffect : EffectTemplate
{
    public uint CinemaId { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        // aaemu-cluster#92: retail plays e.g. the Sharpwind Mines final-boss intro (cinema 51,
        // id_262_01) through this effect — Okape self-casts skill 19535 (200m AoE) on spawn and
        // every player in range must receive the sequence-start packet. The client cannot derive
        // the cinema from the skill fire alone (its compact lacks cinema_effects). The
        // SCPlaySequencePacket payload is a best-effort u32 id; see the packet's remarks.
        if (target is not Character character)
        {
            Logger.Trace($"CinemalEffect {CinemaId}: non-character target {target?.ObjId}, skipping");
            return;
        }

        character.CurrentlyPlayingCinemaId = CinemaId;
        character.SendPacket(new SCPlaySequencePacket(CinemaId));
        Logger.Info($"CinemalEffect: playing cinema {CinemaId} for {character.Name}");
    }
}
