using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
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

        // Will delay for 150 Milliseconds to eliminate the hanging of the skill
        using var source = new CancellationTokenSource();
        var t = Task.Run(async delegate
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), source.Token);
            return 0;
        });
        try
        {
            t.Wait();
        }
        catch (AggregateException ae)
        {
            foreach (var e in ae.InnerExceptions)
                Logger.Trace("{0}: {1}", e.GetType().Name, e.Message);
        }

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
                skillResult = skill.Use(Connection.ActiveChar, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorValue);
            }
        }
        else if (skillCaster is SkillCasterMount scm)
        {
            // Mount or Slave skill
            Logger.Trace($"SkillCasterMount - MountSkillTemplateId {scm.MountSkillTemplateId}");
            skill = new Skill(SkillManager.Instance.GetSkillTemplate(skillId));

            var caster = world.GetBaseUnit(skillCaster.ObjId);
            var mate = caster as Mate;
            var slave = caster as Slave;
            var mountAttachedSkill = 0u;

            if (mate != null || slave != null)
            {
                // check if it's a mate or slave skill and return its rider/operator related skill
                mountAttachedSkill = MateGameData.Instance.GetMountAttachedSkills(skillId, Connection.ActiveChar?.AttachedPoint ?? AttachPointKind.None);
            }

            // Use the main skill on the mate/slave
            var mountPrimaryResult = skill.Use(caster, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorValue);
            if (mountPrimaryResult is SkillResult.SkillReqFail or SkillResult.ZoneBanned)
            {
                // An authored rejection also prevents the linked rider action.
                SendFailure(skillId, skillCaster, skillCastTarget, skill, skillObject, mountPrimaryResult, skillResultErrorValue);
                return;
            }
            if (mountPrimaryResult != SkillResult.Success)
            {
                // skill.Stop(caster, null, skillCaster);
            }
            else if (slave != null)
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
            skillResultErrorValue = 0;
            var riderTemplate = SkillManager.Instance.GetSkillTemplate(skillId);
            skill = new Skill(riderTemplate ?? new SkillTemplate { Id = skillId });
            skillResult = riderTemplate == null ? SkillResult.InvalidSkill :
                skill.Use(rider, skillCaster, skillCastTarget, skillObject, true, out skillResultErrorValue);
        }
        else if (Connection.ActiveChar.IsAutoAttack && skillId == Connection.ActiveChar.AutoAttackTask?.Skill?.Template?.Id)
        {
            // Same as already executing auto-skill, just send the success result.
            skill = Connection.ActiveChar.AutoAttackTask.Skill;
            skillResult = SkillResult.Success;
        }
        else if (SkillManager.Instance.IsDefaultSkill(skillId) || SkillManager.Instance.IsCommonSkill(skillId) && skillCaster is not SkillItem)
        {
            // Is it a common skill?
            skill = new Skill(SkillManager.Instance.GetSkillTemplate(skillId)); // TODO: переделать / rewrite ...
            skillResult = skill.Use(Connection.ActiveChar, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorValue);
            if (skillResult == SkillResult.Success && skillId < 5000 && skillCaster.ObjId == Connection.ActiveChar.ObjId)
            {
                // All basic combat skills are below ID 5000, only 2 (melee),3 (offhand) and 4 (ranged) exist, next actual skill used is 5001
                Connection.ActiveChar.IsAutoAttack = true;
                Connection.ActiveChar.StartAutoSkill(skill);
            }
        }
        else if (skillCaster is SkillItem)
        {
            // Skill.Use validates the actual owned item, its authored skill, and the
            // exact portal-book exceptions before any costs or effects.
            var player = Connection.ActiveChar;
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            skill = new Skill(template ?? new SkillTemplate { Id = skillId });
            skillResult = template == null ? SkillResult.InvalidSkill :
                skill.Use(player, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorValue);
        }
        else if (Connection.ActiveChar.Skills.Skills.ContainsKey(skillId))
        {
            // Is it one of our learned character skills?
            Connection.ActiveChar.Skills.ComboState.Clear();
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            skill = new Skill(template, Connection.ActiveChar);
            skillResult = skill.Use(Connection.ActiveChar, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorValue);
        }
        else if (skillId > 0 && Connection.ActiveChar.Skills.IsVariantOfSkill(skillId))
        {
            // Variant of learned skill?
            Connection.ActiveChar.Skills.ComboState.Clear();
            skill = new Skill(SkillManager.Instance.GetSkillTemplate(skillId));
            skillResult = skill.Use(Connection.ActiveChar, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorValue);
        }
        else
        {
            // No idea what this is
            Logger.Warn($"StartSkill: Id {skillId}, undefined use type");
            // If it's a valid skill cast it. This fixes interactions with quest items/doodads.
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            if (!CanUseUnlearnedSkill(template))
            {
                skill = new Skill(new SkillTemplate { Id = skillId });
                skillResult = SkillResult.InvalidSkill;
            }
            else
            {
                skill = new Skill(template);
                skillResult = skill.Use(Connection.ActiveChar, skillCaster, skillCastTarget, skillObject, false, out skillResultErrorValue);
            }
        }

        if (skillResult != SkillResult.Success)
            SendFailure(skillId, skillCaster, skillCastTarget, skill, skillObject, skillResult, skillResultErrorValue);
    }

    private void SendFailure(uint skillId, SkillCaster caster, SkillCastTarget target, Skill skill,
        SkillObject skillObject, SkillResult result, uint detail)
    {
        // The confirmed failure body is a skill-started packet without a fired/stopped packet.
        var packet = new SCSkillStartedPacket(skillId, 0, caster, target, skill, skillObject)
        {
            RealCastTimeDiv10 = 0, BaseCastTimeDiv10 = 0
        };
        packet.SetSkillResult(result);
        packet.SetResultUInt(detail);
        Connection.ActiveChar.SendPacket(packet);
    }

    internal static bool TryAuthorizeComboFollowup(SkillComboState state, uint skillId, bool isOwnUnitCast)
    {
        return isOwnUnitCast && state.TryConsume(skillId);
    }

    internal static bool CanUseUnlearnedSkill(SkillTemplate template)
    {
        return template is not null &&
            (!template.NeedLearn || template.AbilityId is AbilityType.General or AbilityType.None);
    }
}
