using System.Numerics;

using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.Slaves;

public readonly record struct SlavePlacementRequest(Vector3 Position, float Yaw)
{
    public static bool TryReadTarget(SkillCastTarget target, out SlavePlacementRequest request)
    {
        request = default;
        if (target is not SkillCastPositionTarget position || position.ObjId1 != 0 || position.ObjId2 != 0)
            return false;
        request = new(new(position.PosX, position.PosY, position.PosZ), position.PosRot);
        return SlavePlacementRules.IsFinite(request.Position) && float.IsFinite(request.Yaw);
    }
}
