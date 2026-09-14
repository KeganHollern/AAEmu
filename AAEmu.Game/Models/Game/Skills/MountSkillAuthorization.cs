using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills;

public static class MountSkillAuthorization
{
    public static bool TryAuthorize(Character actor, BaseUnit requestedCaster, SkillCasterMount request,
        uint skillId, out uint riderSkillId)
    {
        riderSkillId = 0;
        if (requestedCaster is not Unit mount || mount is not (Units.Mate or Slave) ||
            request.ObjId != mount.ObjId || !MountSeatAuthorization.SameLivingWorld(actor, mount))
            return false;

        var data = MateGameData.Instance;
        var mountSkillId = request.MountSkillTemplateId;
        if (mountSkillId == 0 || skillId == 0 || data.GetSkillId(mountSkillId) != skillId)
            return false;

        var template = SkillManager.Instance.GetSkillTemplate(skillId);
        if (template == null || mount.Level < template.AbilityLevel)
            return false;

        lock (mount.AttachmentSyncRoot)
        {
            if (mount.AttachmentsRetired)
                return false;
            var seat = AttachPointKind.None;
            if (mount is Units.Mate mate)
            {
                if (!ReferenceEquals(actor.ParentWorld.MateManager.GetActiveMateByMateObjId(mate.ObjId), mate))
                    return false;
                var owned = actor.ParentWorld.MateManager.IsOwnedMate(actor, mate);
                if (MountSeatAuthorization.IsAttached(actor, mate) &&
                    mate.Passengers.TryGetValue(actor.AttachedPoint, out var passenger) &&
                    passenger._objId == actor.ObjId)
                    seat = actor.AttachedPoint;
                else if (!owned || actor.AttachedPoint != AttachPointKind.None || actor.IsRiding ||
                    actor.Transform.Parent != null)
                    return false;

                if (seat == AttachPointKind.Driver && !owned)
                    return false;
                if (!owned && seat == AttachPointKind.None)
                    return false;
                if (!mate.Skills.Contains(mountSkillId))
                    return false;
            }
            else if (mount is Slave slave)
            {
                if (!ReferenceEquals(actor.ParentWorld.SlaveManager.GetSlaveByObjId(slave.ObjId), slave) ||
                    !MountSeatAuthorization.IsAttached(actor, slave) ||
                    !slave.AttachedCharacters.TryGetValue(actor.AttachedPoint, out var passenger) ||
                    !ReferenceEquals(passenger, actor))
                    return false;
                seat = actor.AttachedPoint;

                // A buff replaces the vehicle action bar. It does not add to the base list.
                var buffId = slave.ActiveMountSkillBuffId;
                if (buffId != 0)
                {
                    if (!slave.Buffs.HasEffectsMatchingCondition(buff => buff.InUse && !buff.IsEnded() &&
                            buff.Template?.Id == buffId && (buff.Duration == 0 || buff.GetTimeLeft() > 0)) ||
                        !data.BuffGrantsMountSkill(buffId, mountSkillId))
                        return false;
                }
                else if (!SlaveGameData.Instance.GetSlaveMountSkillList(slave.TemplateId).Contains(mountSkillId))
                    return false;
            }

            return data.TryGetRiderSkill(mountSkillId, seat, out riderSkillId);
        }
    }
}
