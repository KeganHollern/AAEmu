using System.Numerics;

using AAEmu.Game.Models.Game.Models;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>The living collider from the client's authored stance dimensions.</summary>
public static class CryActorGeometry
{
    public static CryGeometryAsset Create(GameStance stance, float scale)
    {
        ArgumentNullException.ThrowIfNull(stance);
        if (!float.IsFinite(scale) || scale <= 0 ||
            !float.IsFinite(stance.Size.X) || stance.Size.X < 0 ||
            !float.IsFinite(stance.Size.Z) || stance.Size.Z < 0 ||
            !float.IsFinite(stance.HeightCollider) || !float.IsFinite(stance.HeightPivot))
            throw new InvalidDataException("Invalid actor stance collision dimensions.");

        // CLivingEntity::SetParams uses sizeCollider.x for both horizontal axes.
        // sizeCollider.z is the cylinder half-height, including for a capsule.
        var radius = stance.Size.X * scale;
        var halfHeight = stance.Size.Z * scale;
        var center = new Vector3(0, 0, (stance.HeightCollider - stance.HeightPivot) * scale);
        var halfSize = new Vector3(radius, radius, halfHeight + (stance.UseCapsule ? radius : 0));
        var shape = new CryCylinder(center, Vector3.UnitZ, radius, halfHeight, stance.UseCapsule);
        return new CryGeometryAsset(new CryBounds(center - halfSize, center + halfSize),
            [new CryGeometryPart(shape, Matrix4x4.Identity, 0x1000, "", "living")]);
    }
}
