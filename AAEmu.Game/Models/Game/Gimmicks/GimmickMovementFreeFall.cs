using System.Numerics;

namespace AAEmu.Game.Models.Game.Gimmicks;

#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
/// <summary>
/// Server-side ballistic fall for skill-spawned gimmicks (Nerta's bombs, Colossus rocks etc.).
/// There is no physics simulation for gimmicks, so without this they would hang frozen at their
/// spawn offset; integrate gravity ourselves and settle at the anchor unit's ground height,
/// firing the template's collision skill on impact. (aaemu-cluster#92)
/// </summary>
public class GimmickMovementFreeFall(Gimmick owner, float groundZ) : GimmickMovementHandler(owner)
#pragma warning restore CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
{
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

        if (_landed)
            owner.OnGroundCollision(impactSpeed);
    }
}
