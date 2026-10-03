using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Slaves;

internal sealed record TurretAimState(float Pitch, float Yaw);

internal static class TurretAimControl
{
    // Exact float at r208022 3999cf98. The native producer multiplies authored degrees by it.
    internal const float DegreesToRadians = 0.01745329238474369f;

    internal static bool TryApply(Character actor, uint unitId, float pitch, float yaw)
    {
        if (actor == null)
            return false;
        // Match BindSlave/UnbindSlave: actor first, then vehicle.
        lock (actor.AttachmentSyncRoot)
        {
            if (actor.ParentWorld?.GetSlaveByObjId(unitId) is not Slave slave)
                return false;
            lock (slave.AttachmentSyncRoot)
            {
                if (slave.AttachmentsRetired || !MountSeatAuthorization.SameLivingWorld(actor, slave) ||
                    !ReferenceEquals(actor.ParentWorld.GetSlaveByObjId(unitId), slave) ||
                    !MountSeatAuthorization.IsAttached(actor, slave) ||
                    !slave.AttachedCharacters.TryGetValue(actor.AttachedPoint, out var occupant) ||
                    !ReferenceEquals(occupant, actor) ||
                    !Accepts(ModelManager.Instance.GetVehicleModels(slave.ModelId), pitch, yaw))
                    return false;

                var aim = new TurretAimState(pitch, yaw);
                if (slave.TurretAim == aim)
                    return true;
                slave.TurretAim = aim;
                slave.BroadcastPacket(new SCUnitModelPostureChangedPacket(slave, 0, false), true);
                return true;
            }
        }
    }

    internal static bool Accepts(VehicleModel model, float pitch, float yaw)
    {
        if (model == null || !float.IsFinite(pitch) || !float.IsFinite(yaw) ||
            !ValidLimits(model.TurretPitchAngleMin, model.TurretPitchAngleMax) ||
            !ValidLimits(model.TurretYawAngleMin, model.TurretYawAngleMax))
            return false;

        if (pitch < model.TurretPitchAngleMin * DegreesToRadians ||
            pitch > model.TurretPitchAngleMax * DegreesToRadians)
            return false;

        // 39425490 clamps mouse aim to authored limits, including a full-circle span.
        if (yaw >= model.TurretYawAngleMin * DegreesToRadians &&
            yaw <= model.TurretYawAngleMax * DegreesToRadians)
            return true;

        // 39425580 wraps installed-turret keyboard aim to (-pi, pi] for a full-circle span.
        // Keep either producer's valid value. Do not clamp or rewrite a forged request.
        return model.InstalledTurret && model.TurretYawAngleMax >= model.TurretYawAngleMin + 360f &&
            yaw > -MathF.PI && yaw <= MathF.PI;
    }

    private static bool ValidLimits(float min, float max) =>
        float.IsFinite(min) && float.IsFinite(max) && min <= max &&
        float.IsFinite(min * DegreesToRadians) && float.IsFinite(max * DegreesToRadians);
}
