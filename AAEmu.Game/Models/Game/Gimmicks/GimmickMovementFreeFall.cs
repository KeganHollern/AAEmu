using System.Numerics;

namespace AAEmu.Game.Models.Game.Gimmicks;

#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
/// <summary>
/// Server-side ballistic fall for skill-spawned gimmicks (Nerta's bombs, Colossus rocks etc.).
/// There is no physics simulation for gimmicks, so without this they would hang frozen at their
/// spawn offset; integrate gravity ourselves and settle on the landing plane geodata reported under
/// the spawn position, notifying the gimmick on impact. (aaemu-cluster#92)
/// </summary>
public class GimmickMovementFreeFall(Gimmick owner, float groundZ) : GimmickMovementHandler(owner)
#pragma warning restore CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
{
    /// <summary>
    /// Minimum drop height worth simulating. Anything shallower is already resting on its landing
    /// plane, so simulating it would only "land" it on the very first tick.
    /// </summary>
    public const float MinimumDropHeight = 0.1f;

    /// <summary>
    /// Whether a gimmick spawned at <paramref name="spawnZ"/> has anything to fall through before
    /// reaching <paramref name="groundZ"/>. Gimmicks authored at or below their landing plane
    /// (spawn_gimmick_effects.offset_z &lt; 0) must keep their pre-physics behaviour: no handler, no
    /// motion, and above all no instant landing that would preempt a skill_delay fuse.
    /// (aaemu-cluster#92)
    /// </summary>
    public static bool ShouldSimulate(float spawnZ, float groundZ)
    {
        return float.IsFinite(spawnZ) && float.IsFinite(groundZ) && spawnZ > groundZ + MinimumDropHeight;
    }

    private Vector3 _velocity = owner.Vel;
    private bool _landed;

    public override void Tick(TimeSpan delta)
    {
        if (_landed)
            return;

        var deltaTime = (float)delta.TotalSeconds;
        if (deltaTime <= 0f)
            return;

        _velocity.Z -= (owner.Template?.Gravity ?? 9.8f) * deltaTime;
        var position = owner.Transform.World.Position;
        var next = position + _velocity * deltaTime;
        var impactSpeed = MathF.Abs(_velocity.Z);
        if (next.Z <= groundZ)
        {
            next.Z = groundZ;
            _landed = true;
            _velocity = Vector3.Zero;
        }

        owner.Transform.Local.Translate(next - position);
        owner.Vel = _velocity;
        // Motion state is uniform across handlers: GimmickTick reports a velocity only while the
        // handler says the object is moving, so clients stop dead-reckoning the instant we park it.
        // (aaemu-cluster#92)
        owner.IsMoving = !_landed;

        if (_landed)
            owner.OnGroundCollision(impactSpeed);
    }
}
