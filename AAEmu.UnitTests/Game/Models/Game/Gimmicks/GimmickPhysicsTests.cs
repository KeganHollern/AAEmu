using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Gimmicks;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Gimmicks;

/// <summary>
/// Invariants for the server-side gimmick ballistics added for aaemu-cluster#92. All template
/// numbers are the compact <c>gimmicks</c> / <c>spawn_gimmick_effects</c> rows they are named after.
/// </summary>
public class GimmickPhysicsTests
{
    #region Fix 2 - only simulate a fall when there is something to fall through

    [Test]
    public async Task ShouldSimulate_SpawnAboveLandingPlane_Simulates()
    {
        // spawn_gimmick_effects 140 (gimmick 50, Colossus rock): offset_z = +15
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(115f, 100f)).IsTrue();
        // spawn_gimmick_effects 154 (gimmick 47, Nerta's bomb): offset_z = +12
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(112f, 100f)).IsTrue();
    }

    [Test]
    public async Task ShouldSimulate_SpawnBelowLandingPlane_DoesNotSimulate()
    {
        // skill 13430 'Bombard' -> spawn_gimmick_effects 66 (gimmick 30): offset_z = -3.0
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(97f, 100f)).IsFalse();
        // skill 15339 'Fireball' -> spawn_gimmick_effects 92 (gimmick 38): offset_z = -2.0
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(98f, 100f)).IsFalse();
    }

    [Test]
    public async Task ShouldSimulate_SpawnOnLandingPlane_DoesNotSimulate()
    {
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(100f, 100f)).IsFalse();
        await Assert.That(
            GimmickMovementFreeFall.ShouldSimulate(100f + GimmickMovementFreeFall.MinimumDropHeight, 100f)).IsFalse();
    }

    [Test]
    public async Task ShouldSimulate_NonFiniteHeights_DoesNotSimulate()
    {
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(float.NaN, 100f)).IsFalse();
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(100f, float.NaN)).IsFalse();
        await Assert.That(GimmickMovementFreeFall.ShouldSimulate(float.PositiveInfinity, 100f)).IsFalse();
    }

    #endregion

    #region Fix 3 - a landing must never preempt or cancel a pending fuse

    [Test]
    public async Task OnGroundCollision_CollisionUnitOnly_DoesNotDespawn()
    {
        // gimmick 7 'mine': collision_skill_id 12006, collision_unit_only=t, disappear_by_collision=t
        var gimmick = CreateGimmick(new GimmickTemplate
        {
            Id = 7,
            Gravity = 9.8f,
            CollisionSkillId = 12006,
            CollisionMinSpeed = 1.0f,
            CollisionUnitOnly = true,
            DisappearByCollision = true,
            FadeOutDuration = 1000
        });

        await Assert.That(gimmick.GroundCollisionReactionAllowed).IsFalse();

        gimmick.OnGroundCollision(15f);

        await Assert.That(gimmick.Despawn).IsEqualTo(DateTime.MinValue);
    }

    [Test]
    public async Task OnGroundCollision_FusePending_DoesNotDespawnOrConsumeTheFuse()
    {
        // skill 13430 'Bombard' -> gimmick 30: skill_id 14648 == collision_skill_id,
        // skill_delay 10000, disappear_by_collision=t, collision_unit_only=f
        var gimmick = CreateGimmick(new GimmickTemplate
        {
            Id = 30,
            Gravity = 9.8f,
            SkillId = 14648,
            SkillDelay = 10000,
            CollisionSkillId = 14648,
            CollisionMinSpeed = 1.0f,
            CollisionUnitOnly = false,
            DisappearByCollision = true,
            FadeOutDuration = 1000
        });

        await Assert.That(gimmick.IsFusePending).IsTrue();
        await Assert.That(gimmick.GroundCollisionReactionAllowed).IsFalse();

        gimmick.OnGroundCollision(15f);

        // The 10s fuse is still armed and the disappear_by_collision despawn did not steal the bomb
        await Assert.That(gimmick.IsFusePending).IsTrue();
        await Assert.That(gimmick.Despawn).IsEqualTo(DateTime.MinValue);
    }

    [Test]
    public async Task GroundCollisionReactionAllowed_ColossusRock_StillReactsToImpact()
    {
        // plot 388 -> spawn_gimmick_effects 140/141/142 -> gimmick 50 'Sharpwind rock':
        // collision_skill_id 18252, collision_min_speed 1.0, collision_unit_only=f, no skill_delay
        var gimmick = CreateGimmick(new GimmickTemplate
        {
            Id = 50,
            Gravity = 9.8f,
            SkillId = 0,
            SkillDelay = 0,
            CollisionSkillId = 18252,
            CollisionMinSpeed = 1.0f,
            CollisionUnitOnly = false,
            DisappearByCollision = true,
            LifeTime = 10000
        });

        await Assert.That(gimmick.IsFusePending).IsFalse();
        await Assert.That(gimmick.GroundCollisionReactionAllowed).IsTrue();
    }

    [Test]
    public async Task OnGroundCollision_ReactingTemplate_SchedulesFadeOutDespawn()
    {
        // gimmick 50's shape without the collision skill, so the despawn arm can be observed
        // without the skill/task subsystems (collision_skill_id is nullable in the compact, e.g.
        // gimmick 47).
        var gimmick = CreateGimmick(new GimmickTemplate
        {
            Id = 50,
            Gravity = 9.8f,
            CollisionSkillId = 0,
            CollisionMinSpeed = 1.0f,
            CollisionUnitOnly = false,
            DisappearByCollision = true,
            FadeOutDuration = 2000
        });

        var before = DateTime.UtcNow;
        gimmick.OnGroundCollision(15f);

        await Assert.That(gimmick.Despawn).IsGreaterThanOrEqualTo(before.AddMilliseconds(2000));
    }

    [Test]
    public async Task IsFusePending_DelayWithoutSkill_IsNotPending()
    {
        // gimmick 28 'flare': skill_delay 1000 but no skill_id, so there is no fuse to protect
        var gimmick = CreateGimmick(new GimmickTemplate
        {
            Id = 28,
            Gravity = 9.8f,
            SkillId = 0,
            SkillDelay = 1000,
            CollisionUnitOnly = false
        });

        await Assert.That(gimmick.IsFusePending).IsFalse();
        await Assert.That(gimmick.GroundCollisionReactionAllowed).IsTrue();
    }

    #endregion

    #region Fix 4 - handlers own their velocity when they park the object

    [Test]
    public async Task ResolveReportedVelocity_HandlerParkedObject_ReportsZero()
    {
        var gimmick = CreateGimmick(null);
        gimmick.MovementHandler = new GimmickMovementElevator(gimmick);
        gimmick.IsMoving = false;

        // 4.5 m/s worth of finite difference left over from the elevator's last moving tick
        var reported = gimmick.ResolveReportedVelocity(new Vector3(0f, 0f, 0.45f), 0.1f);

        await Assert.That(reported).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task Elevator_ArrivalTick_ReportsZeroInsteadOfStaleFiniteDifference()
    {
        // main_world elevator 007_008: BottomZ 261.73, TopZ 294.17, MiddleZ 0, WaitTime 6.0
        var gimmick = CreateGimmick(null);
        var spawner = new GimmickSpawner(gimmick.ParentWorld)
        {
            TopZ = 294.17f, MiddleZ = 0f, BottomZ = 261.73f, WaitTime = 6.0f
        };
        gimmick.Spawner = spawner;
        gimmick.Transform.Local.SetPosition(new Vector3(8144.58679f, 9195.4581f, 261.73f));
        var handler = new GimmickMovementElevator(gimmick);
        gimmick.MovementHandler = handler;

        var step = TimeSpan.FromMilliseconds(100);
        var deltaTime = (float)step.TotalSeconds;
        Vector3 lastPos;
        Vector3 reported;
        var arrived = false;
        for (var i = 0; i < 1000; i++)
        {
            lastPos = gimmick.Transform.World.Position;
            handler.Tick(step);
            reported = gimmick.ResolveReportedVelocity(gimmick.Transform.World.Position - lastPos, deltaTime);
            if (gimmick.IsMoving)
            {
                await Assert.That(reported.Z).IsEqualTo(4.5f).Within(0.001f);
                continue;
            }

            // Arrival tick: the platform is parked, so the client must not dead-reckon it any
            // further during the 6s WaitTime in which TimeLeft suppresses corrective packets.
            arrived = true;
            await Assert.That(reported).IsEqualTo(Vector3.Zero);
            await Assert.That(gimmick.Transform.World.Position.Z).IsEqualTo(294.17f).Within(0.001f);
            break;
        }

        await Assert.That(arrived).IsTrue();
    }

    [Test]
    public async Task ResolveReportedVelocity_HandlerStillMoving_ReportsFiniteDifference()
    {
        var gimmick = CreateGimmick(null);
        gimmick.MovementHandler = new GimmickMovementElevator(gimmick);
        gimmick.IsMoving = true;

        var reported = gimmick.ResolveReportedVelocity(new Vector3(0f, 0f, 0.45f), 0.1f);

        await Assert.That(reported.Z).IsEqualTo(4.5f).Within(0.0001f);
    }

    [Test]
    public async Task ResolveReportedVelocity_NoHandler_KeepsFiniteDifference()
    {
        // NPC-AI driven gimmicks (Gimmick.MoveTowards) have no handler and must be unaffected
        var gimmick = CreateGimmick(null);
        gimmick.IsMoving = false;

        var reported = gimmick.ResolveReportedVelocity(new Vector3(1f, 0f, 0f), 0.5f);

        await Assert.That(reported.X).IsEqualTo(2f).Within(0.0001f);
    }

    [Test]
    public async Task ResolveReportedVelocity_ZeroDelta_ReportsZero()
    {
        var gimmick = CreateGimmick(null);
        gimmick.IsMoving = true;

        await Assert.That(gimmick.ResolveReportedVelocity(new Vector3(1f, 2f, 3f), 0f)).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task FreeFall_DrivesIsMovingWhileFallingAndClearsItOnLanding()
    {
        // gimmick 47, Nerta's bomb: collision_unit_only=t so the landing callback stays local
        var gimmick = CreateGimmick(new GimmickTemplate
        {
            Id = 47,
            Gravity = 9.8f,
            SkillId = 18577,
            SkillDelay = 3000,
            LifeTime = 3100,
            CollisionUnitOnly = true,
            DisappearByCollision = false
        });
        gimmick.Transform.Local.SetPosition(new Vector3(1024f, 1024f, 112f)); // offset_z = +12
        var handler = new GimmickMovementFreeFall(gimmick, 100f);
        gimmick.MovementHandler = handler;

        var step = TimeSpan.FromMilliseconds(100);
        var before = gimmick.Transform.World.Position;
        handler.Tick(step);
        var fallen = gimmick.Transform.World.Position - before;

        await Assert.That(gimmick.IsMoving).IsTrue();
        await Assert.That(gimmick.Transform.World.Position.Z).IsLessThan(112f);
        await Assert.That(gimmick.ResolveReportedVelocity(fallen, (float)step.TotalSeconds).Z).IsLessThan(0f);

        for (var i = 0; i < 200 && gimmick.IsMoving; i++)
            handler.Tick(step);

        await Assert.That(gimmick.IsMoving).IsFalse();
        await Assert.That(gimmick.Transform.World.Position.Z).IsEqualTo(100f).Within(0.0001f);
        // The fuse survived the landing, so the bomb still detonates on its authored 3s delay
        await Assert.That(gimmick.IsFusePending).IsTrue();
        await Assert.That(gimmick.Despawn).IsEqualTo(DateTime.MinValue);
    }

    #endregion

    #region Fix 6 - velocity_coordiate_id

    [Test]
    public async Task ResolveInitialVelocity_WorldAxisType_IsUnrotated()
    {
        // spawn_gimmick_effects 140-142 (gimmick 50) are velocity_coordiate_id 0 with zero velocity
        var resolved = GimmickSpawner.ResolveInitialVelocity(
            VelocityCoordinateType.Unk0, new Vector3(3f, 3f, 0f), MathF.PI / 2f);

        await Assert.That(resolved).IsEqualTo(new Vector3(3f, 3f, 0f));
    }

    [Test]
    public async Task ResolveInitialVelocity_CasterRelativeType_RotatesByAnchorYaw()
    {
        // spawn_gimmick_effects 154/155 (gimmick 47): velocity_coordiate_id 2, velocity_x +3/-3,
        // velocity_y 3. At yaw 90 degrees "front" is world -X and "right" is world +Y.
        var right = GimmickSpawner.ResolveInitialVelocity(
            VelocityCoordinateType.Unk2, new Vector3(3f, 3f, 0f), MathF.PI / 2f);
        var left = GimmickSpawner.ResolveInitialVelocity(
            VelocityCoordinateType.Unk2, new Vector3(-3f, 3f, 0f), MathF.PI / 2f);

        await Assert.That(right.X).IsEqualTo(-3f).Within(0.0001f);
        await Assert.That(right.Y).IsEqualTo(3f).Within(0.0001f);
        await Assert.That(left.X).IsEqualTo(-3f).Within(0.0001f);
        await Assert.That(left.Y).IsEqualTo(-3f).Within(0.0001f);
    }

    [Test]
    public async Task ResolveInitialVelocity_CasterRelativeAtZeroYaw_MatchesWorldAxes()
    {
        var resolved = GimmickSpawner.ResolveInitialVelocity(
            VelocityCoordinateType.Unk2, new Vector3(3f, 3f, 1.5f), 0f);

        await Assert.That(resolved.X).IsEqualTo(3f).Within(0.0001f);
        await Assert.That(resolved.Y).IsEqualTo(3f).Within(0.0001f);
        await Assert.That(resolved.Z).IsEqualTo(1.5f);
    }

    [Test]
    public async Task ResolveInitialVelocity_CasterRelative_PreservesMirrorSymmetryAtAnyYaw()
    {
        // Nerta's two bombs must always fan out symmetrically around her facing
        const float yaw = 1.234f;
        var right = GimmickSpawner.ResolveInitialVelocity(
            VelocityCoordinateType.Unk2, new Vector3(3f, 3f, 0f), yaw);
        var left = GimmickSpawner.ResolveInitialVelocity(
            VelocityCoordinateType.Unk2, new Vector3(-3f, 3f, 0f), yaw);

        await Assert.That(right.Length()).IsEqualTo(left.Length()).Within(0.0001f);
        await Assert.That(Vector3.Dot(right, left)).IsEqualTo(0f).Within(0.0001f);
    }

    #endregion

    private static Gimmick CreateGimmick(GimmickTemplate template)
    {
        var worldTemplate = new WorldTemplate
        {
            Id = 1,
            Name = "gimmick_physics_test",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };
        var world = new WorldInstance(worldTemplate, 1, true, 1);
        world.SpawnManager = new SpawnManager(world);

        var gimmick = new Gimmick { Template = template, TemplateId = template?.Id ?? 0 };
        typeof(global::AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(gimmick, world);
        gimmick.Transform.Local.SetPosition(new Vector3(1024f, 1024f, 100f));
        return gimmick;
    }
}
