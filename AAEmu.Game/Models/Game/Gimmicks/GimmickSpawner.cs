using System.Numerics;
using System.Text.Json.Serialization;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using NLog;

#pragma warning disable IDE0079 // Remove unnecessary suppression

namespace AAEmu.Game.Models.Game.Gimmicks;

public class GimmickSpawner : Spawner<Gimmick>
{
    [JsonIgnore]
    protected static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    [JsonIgnore]
    public WorldInstance ParentWorld { get; set; }

    public uint GimmickId { get; set; } // here we mean TemplateId
    public long EntityGuid { get; set; }
    public float WaitTime { get; set; }
    public float TopZ { get; set; }
    public float MiddleZ { get; set; }
    public float BottomZ { get; set; }
    public float RotationX { get; set; }
    public float RotationY { get; set; }
    public float RotationZ { get; set; }
    public float RotationW { get; set; }
    //public Quaternion Rot { get; set; }
    public float Scale { get; set; }
    [JsonIgnore]
    public Gimmick Last { get; set; }
    public uint Count { get; set; }
    public bool OffsetFromSource { get; set; }
    public OffsetCoordinateType OffsetCoordinateId { get; set; }
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }
    public VelocityCoordinateType VelocityCoordinateId { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public float VelocityZ { get; set; }
    public AngVelCoordinateType AngVelCoordinateId { get; set; }
    public float AngVelX { get; set; }
    public float AngVelY { get; set; }
    public float AngVelZ { get; set; }

    public GimmickSpawner()
    {
        // DefaultConstructor for JSON reading
    }
    public GimmickSpawner(WorldInstance parentWorld, SpawnGimmickEffect sgEffect, BaseUnit caster, BaseUnit target = null)
    {
        ParentWorld = parentWorld;
        GimmickId = sgEffect.GimmickId;
        OffsetFromSource = sgEffect.OffsetFromSource;
        OffsetCoordinateId = (OffsetCoordinateType)sgEffect.OffsetCoordinateId;
        OffsetX = sgEffect.OffsetX;
        OffsetY = sgEffect.OffsetY;
        OffsetZ = sgEffect.OffsetZ;
        Scale = sgEffect.Scale;
        VelocityCoordinateId = (VelocityCoordinateType)sgEffect.VelocityCoordinateId;
        VelocityX = sgEffect.VelocityX;
        VelocityY = sgEffect.VelocityY;
        VelocityZ = sgEffect.VelocityZ;
        AngVelCoordinateId = (AngVelCoordinateType)sgEffect.AngVelCoordinateId;
        AngVelX = sgEffect.AngVelX;
        AngVelY = sgEffect.AngVelY;
        AngVelZ = sgEffect.AngVelZ;
        Count = 1;

        var gimmick = ParentWorld.GimmickManager.Create(GimmickId);
        if (gimmick == null)
        {
            Logger.Warn($"SpawnGimmickEffect: gimmick template {GimmickId} does not exist");
            return;
        }
        gimmick.Spawner = this;
        gimmick.Spawner.RespawnTime = 0; // don't respawn
        // Offsets with offset_from_source=false are anchored on the effect's target (e.g. Nerta's
        // bombs drop from above the player), not on the caster (aaemu-cluster#92)
        var anchor = OffsetFromSource || target?.Transform == null ? caster : target;
        gimmick.Transform = anchor.Transform.CloneDetached(gimmick);
        var anchorZ = gimmick.Transform.World.Position.Z;
        var anchorYaw = gimmick.Transform.World.Rotation.Z;
        gimmick.EntityGuid = 0;
        gimmick.SpawnerUnitId = caster.ObjId;
        gimmick.GrasperUnitId = 0;
        switch (OffsetCoordinateId)
        {
            case OffsetCoordinateType.Unk0:
                var (newX0, newY0, newZ0) = PositionAndRotation.AddDistanceToFront(1, 1, gimmick.Transform.World.Position, gimmick.Transform.World.Position);
                gimmick.Transform.World.Position = new Vector3(newX0, newY0, newZ0);
                break;
            case OffsetCoordinateType.Unk1: // world-axis offset, previously dropped (aaemu-cluster#92)
            case OffsetCoordinateType.Unk2:
                gimmick.Transform.Local.AddDistance(OffsetX, OffsetY, OffsetZ);
                break;
            case OffsetCoordinateType.Unk3:
                break;
            default:
#pragma warning disable CA2208 // Instantiate argument exceptions correctly
                throw new ArgumentOutOfRangeException();
#pragma warning restore CA2208 // Instantiate argument exceptions correctly
        }

        // Resolve the landing plane from geodata under the gimmick's FINAL position. The anchor's
        // own Z is not a landing plane: a negative offset_z starts the gimmick below the caster's
        // feet (skill 13430 'Bombard' at -3.0, skill 15339 'Fireball' at -2.0), and casters on a
        // ship deck, a ledge or a glider are not standing on the surface the gimmick drops onto.
        // (aaemu-cluster#92)
        var groundZ = ResolveLandingPlane(gimmick.Transform.World.Position, anchorZ);

        // Give the gimmick its initial throw velocity so clients render the toss/fall (aaemu-cluster#92)
        gimmick.Vel = ResolveInitialVelocity(VelocityCoordinateId, new Vector3(VelocityX, VelocityY, VelocityZ), anchorYaw);
        gimmick.SetScale(Scale);
        // No physics engine for gimmicks: animate the drop server-side, but only when the gimmick
        // actually starts above its landing plane. A gimmick spawned at or below the plane has
        // nothing to fall through, and an instant "landing" would preempt its authored skill_delay
        // fuse and teleport it back up to the caster. (aaemu-cluster#92)
        if ((gimmick.Template?.Gravity ?? 0f) > 0f &&
            GimmickMovementFreeFall.ShouldSimulate(gimmick.Transform.World.Position.Z, groundZ))
            gimmick.MovementHandler = new GimmickMovementFreeFall(gimmick, groundZ);
        gimmick.Spawn(); // добавляем в мир
        ParentWorld.GimmickManager.AddActiveGimmick(gimmick);

        if (caster is Npc npc)
        {
            npc.Gimmick = gimmick;
        }
    }

    /// <summary>
    /// Landing plane for a skill-spawned gimmick: the ground that geodata reports under the
    /// gimmick's own final position, falling back to the anchor unit's height when geodata cannot
    /// answer (no heightmap loaded, position off-map). Sampling at the anchor's pre-offset Z would
    /// place gimmicks with a negative offset_z below their own landing plane. (aaemu-cluster#92)
    /// </summary>
    private float ResolveLandingPlane(Vector3 finalPosition, float anchorZ)
    {
        if (ParentWorld?.Template?.GeoData?.TryGetGroundHeight(finalPosition, out var sampledZ) == true &&
            float.IsFinite(sampledZ))
            return sampledZ;

        return anchorZ;
    }

    /// <summary>
    /// Initial throw velocity in world axes. velocity_coordiate_id 0 is already world-relative,
    /// everything else is relative to the anchor's facing (77 of 109 spawn_gimmick_effects rows),
    /// so rotate the horizontal pair by the anchor's yaw exactly like
    /// <see cref="PositionAndRotation.AddDistanceToRight"/> (X = right) combined with
    /// <see cref="PositionAndRotation.AddDistanceToFront"/> (Y = front). Without this, Nerta's
    /// mirrored bombs (rows 154/155, velocity_x +3/-3) fan out along world north instead of across
    /// her facing. (aaemu-cluster#92)
    /// </summary>
    internal static Vector3 ResolveInitialVelocity(VelocityCoordinateType coordinateType, Vector3 velocity, float anchorYaw)
    {
        if (coordinateType == VelocityCoordinateType.Unk0)
            return velocity;

        var sin = MathF.Sin(anchorYaw);
        var cos = MathF.Cos(anchorYaw);
        return new Vector3(
            velocity.X * cos - velocity.Y * sin,
            velocity.X * sin + velocity.Y * cos,
            velocity.Z);
    }

    public GimmickSpawner(WorldInstance parentWorld)
    {
        ParentWorld = parentWorld;
        Count = 1;
    }

    public override Gimmick Spawn(uint objId)
    {
        var gimmick = ParentWorld.GimmickManager.Create(objId, UnitId, this);
        if (gimmick == null)
        {
            Logger.Warn($"Gimmick {UnitId}, from spawn not exist at db");
            return null;
        }

        Last = gimmick;
        return gimmick;
    }

    public override void Despawn(Gimmick gimmick)
    {
        ParentWorld.GimmickManager.RemoveActiveGimmick(gimmick);
        gimmick.Delete();
        if (gimmick.Respawn == DateTime.MinValue)
        {
            if (gimmick.ObjId > 0)
                ObjectIdManager.Instance.ReleaseId(gimmick.ObjId);
            if (gimmick.GimmickId > 0)
                GimmickIdManager.Instance.ReleaseId(gimmick.GimmickId);
        }

        Last = null;
    }

    public void DecreaseCount(Gimmick gimmick)
    {
        if (RespawnTime > 0)
        {
            gimmick.Respawn = DateTime.UtcNow.AddSeconds(RespawnTime);
            ParentWorld.SpawnManager.AddRespawn(gimmick);
        }
        else
        {
            Last = null;
        }

        gimmick.Delete();
    }
}
