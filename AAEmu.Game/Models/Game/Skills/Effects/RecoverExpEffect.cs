using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class RecoverExpEffect : EffectTemplate
{
    public bool NeedMoney { get; init; }
    public bool NeedLaborPower { get; init; }

    public bool NeedPriest { get; init; }
    // TODO: 1.2 specific field Penaltied, not sure how this is used
    // public bool Penaltied { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        if (caster is not Character player)
            return;
        var batch = SkillLaborBatch.For(player);
        if (batch == null)
        {
            if (source?.Skill != null)
                source.Skill.Cancelled = true;
            return;
        }
        if (NeedMoney)
        {
            // Neither authored r208022 recovery effect defines a money charge.
            player.SendErrorMessage(ErrorMessageType.InvalidTarget);
            batch.Fail();
            return;
        }

        // Check for nearby priest if needed (caster and target are always the player)
        if (NeedPriest)
        {
            var npcs = WorldManager.GetAround<Npc>(player, 10f);
            var found = false;
            foreach (var npc in npcs)
            {
                if (npc.Template.Priest)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                player.SendErrorMessage(ErrorMessageType.TooFarAway);
                batch.Fail();
                return;
            }
        }

        // Preserve the current level-based cost. Scroll effect 5 explicitly has no labor charge.
        var neededLaborCost = !NeedLaborPower ? 0 : player.Level <= 50 ? player.Level : 50 + ((player.Level - 50) * 20);
        player.StageExperienceRecovery((short)neededLaborCost);
    }
}
