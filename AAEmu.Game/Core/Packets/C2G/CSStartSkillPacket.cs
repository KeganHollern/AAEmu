using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.SkillControllers;
using AAEmu.Game.Physics.Debug;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSStartSkillPacket() : GamePacket(CSOffsets.CSStartSkillPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // Ignore if there is no active character set
        if (Connection.ActiveChar == null)
            return;

        var skillId = stream.ReadUInt32();

        var skillCasterType = stream.ReadByte();
        var skillCaster = SkillCaster.GetByType((SkillCasterType)skillCasterType);
        skillCaster.Read(stream);

        var skillCastTargetType = stream.ReadByte();
        var skillCastTarget = SkillCastTarget.GetByType((SkillCastTargetType)skillCastTargetType);
        skillCastTarget.Read(stream);

        var flag = stream.ReadByte();
        var flagType = flag & 15;
        var skillObject = SkillObject.GetByType((SkillObjectType)flagType);
        if (flagType > 0) skillObject.Read(stream);

        HarpoonMechanicsDebug.LogCsStartSkillIfHarpoon(skillId, flag, flagType, skillCaster, skillCastTarget, skillObject);

        if (Connection.ActiveChar != null)
            Connection.ActiveChar.LastPacketActivityTime = DateTime.UtcNow;
        var world = Connection.ActiveChar?.ParentWorld ?? WorldManager.Instance.GetWorld(WorldManager.DefaultInstanceId);

        var skillResult = SkillResult.Success;
        ushort skillResultErrorUShort = 0;
        var skillResultErrorValue = 0u;
        Skill skill = null;
        var isOwnUnitCast = skillCaster is SkillCasterUnit && skillCaster.ObjId == Connection.ActiveChar.ObjId;

        if (skillId > 0 && SkillManager.Instance.IsComboFollowupSkill(skillId))
        {
            // Every client-selected path for a compact-defined combo follow-up is gated here,
            // before mount, common, item, learned-skill, variant, or interaction routing.
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            if (template is null ||
                !TryAuthorizeComboFollowup(Connection.ActiveChar.Skills.ComboState, skillId, isOwnUnitCast))
            {
                Logger.Warn($"StartSkill: Character {Connection.ActiveChar.ObjId} attempted unauthorized combo follow-up {skillId}");
                skill = new Skill(new SkillTemplate { Id = skillId });
                skillResult = SkillResult.InvalidSkill;
            }
            else
            {
                skill = new Skill(template, Connection.ActiveChar);
                skillResult = skill.Use(Connection.ActiveChar, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorUShort, out skillResultErrorValue);
            }
        }
        else if (skillCaster is SkillCasterMount scm)
        {
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            var caster = world?.GetBaseUnit(skillCaster.ObjId);
            skill = new Skill(template ?? new SkillTemplate { Id = skillId });
            if (template == null || !MountSkillAuthorization.TryAuthorize(Connection.ActiveChar, caster, scm,
                    skillId, out var mountAttachedSkill))
            {
                Logger.Warn($"StartSkill: Character {Connection.ActiveChar.ObjId} attempted unauthorized mount skill {skillId}");
                SendFailure(skillId, skillCaster, skillCastTarget, skill, skillObject, SkillResult.InvalidSkill, 0, 0);
                return;
            }
            var slave = caster as Slave;

            // Use the main skill on the mate/slave
            var mountPrimaryResult = skill.Use(caster, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorUShort, out skillResultErrorValue);
            if (mountPrimaryResult != SkillResult.Success)
            {
                SendFailure(skillId, skillCaster, skillCastTarget, skill, skillObject, mountPrimaryResult, skillResultErrorUShort, skillResultErrorValue);
                return;
            }
            if (slave != null)
            {
                if (skillId == HarpoonMechanicsDebug.ShipLaunchHarpoonSkillId)
                    ShipHarpoonRopeController.OnLaunchSucceeded(slave, skillCastTarget, Connection.ActiveChar);
                else if (skillId == HarpoonMechanicsDebug.ShipCutHarpoonRopeSkillId)
                    ShipHarpoonRopeController.OnCutRope(slave, Connection.ActiveChar);
            }

            // If no rider/operator skill is linked, we can stop here
            if (mountAttachedSkill == 0)
                return;

            // Use player's currently selected for the rider/operator skill
            var rider = Connection.ActiveChar;
            var riderTarget = rider.CurrentTarget as Unit ?? rider;

            // Keep the actual rider action and authored failure detail for its response.
            skillId = mountAttachedSkill;
            skillCaster = new SkillCasterUnit(rider.ObjId);
            skillCastTarget = new SkillCastUnitTarget(riderTarget.ObjId);
            skillObject = new SkillObject();
            skillResultErrorUShort = 0;
            skillResultErrorValue = 0;
            var riderTemplate = SkillManager.Instance.GetSkillTemplate(skillId);
            skill = new Skill(riderTemplate ?? new SkillTemplate { Id = skillId });
            skillResult = riderTemplate == null ? SkillResult.InvalidSkill :
                skill.Use(rider, skillCaster, skillCastTarget, skillObject, true, out skillResultErrorUShort, out skillResultErrorValue);
        }
        else if (skillCaster is SkillItem)
        {
            // Skill.Use checks the owned item and its exact authored skill before costs.
            var player = Connection.ActiveChar;
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            skill = new Skill(template ?? new SkillTemplate { Id = skillId });
            skillResult = template == null ? SkillResult.InvalidSkill :
                skill.Use(player, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorUShort, out skillResultErrorValue);
        }
        else
        {
            var player = Connection.ActiveChar;
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            skill = new Skill(template ?? new SkillTemplate { Id = skillId }, player);
            if (!SkillCastAuthorization.CanUseCharacterSkill(player, template, skillCaster, skillCastTarget))
            {
                Logger.Warn($"StartSkill: Character {player.ObjId} attempted unauthorized skill {skillId}");
                skillResult = SkillResult.InvalidSkill;
            }
            else if (player.IsAutoAttack && skillId == player.AutoAttackTask?.Skill?.Template?.Id)
            {
                skill = player.AutoAttackTask.Skill;
            }
            else
            {
                if (player.Skills.Skills.ContainsKey(skillId))
                    player.Skills.ComboState.Clear();
                skillResult = skill.Use(player, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorUShort, out skillResultErrorValue);
                if (skillResult == SkillResult.Success && skillId is 2 or 3 or 4)
                {
                    player.IsAutoAttack = true;
                    player.StartAutoSkill(skill);
                }
            }
        }

        if (skillResult != SkillResult.Success)
            SendFailure(skillId, skillCaster, skillCastTarget, skill, skillObject, skillResult, skillResultErrorUShort, skillResultErrorValue);
    }

    private void SendFailure(uint skillId, SkillCaster caster, SkillCastTarget target, Skill skill,
        SkillObject skillObject, SkillResult result, ushort detailUShort, uint detail)
    {
        // The confirmed failure body is a skill-started packet without a fired/stopped packet.
        var packet = new SCSkillStartedPacket(skillId, 0, caster, target, skill, skillObject)
        {
            RealCastTimeDiv10 = 0, BaseCastTimeDiv10 = 0
        };
        packet.SetSkillResult(result);
        packet.SetResultUShort(detailUShort);
        packet.SetResultUInt(detail);
        Connection.ActiveChar.SendPacket(packet);
    }

    internal static bool TryAuthorizeComboFollowup(SkillComboState state, uint skillId, bool isOwnUnitCast)
    {
        return isOwnUnitCast && state.TryConsume(skillId);
    }

}
