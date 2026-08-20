using System.Numerics;

using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public class MateSpawnPositionResolverTests
{
    [Test]
    [Arguments(102.5f)]
    [Arguments(97.5f)]
    public async Task GroundedSpawn_UsesInclineHeightAtFinalHorizontalPosition(float groundHeight)
    {
        var source = new PositionAndRotation(
            new Vector3(10f, 20f, 100f),
            Vector3.Zero);
        Vector3? sampledPosition = null;

        var resolved = MateSpawnPositionResolver.TryResolve(
            source,
            0f,
            3f,
            true,
            position =>
            {
                sampledPosition = position;
                return groundHeight;
            },
            out var spawnPosition);

        await Assert.That(resolved).IsTrue();
        await Assert.That(sampledPosition).IsNotNull();
        await Assert.That(sampledPosition!.Value.X).IsEqualTo(10f).Within(0.001f);
        await Assert.That(sampledPosition.Value.Y).IsEqualTo(23f).Within(0.001f);
        await Assert.That(sampledPosition.Value.Z).IsEqualTo(100f).Within(0.001f);
        await Assert.That(spawnPosition.Position.X).IsEqualTo(10f).Within(0.001f);
        await Assert.That(spawnPosition.Position.Y).IsEqualTo(23f).Within(0.001f);
        await Assert.That(spawnPosition.Position.Z).IsEqualTo(groundHeight).Within(0.001f);
    }

    [Test]
    public async Task GroundedSpawn_UsesSourceHeightToSelectMultiLevelSurface()
    {
        var source = new PositionAndRotation(
            new Vector3(10f, 20f, 89f),
            Vector3.Zero);

        var resolved = MateSpawnPositionResolver.TryResolve(
            source,
            0f,
            3f,
            true,
            position => position.Z > 50f ? 90f : 10f,
            out var spawnPosition);

        await Assert.That(resolved).IsTrue();
        await Assert.That(spawnPosition.Position.Z).IsEqualTo(90f);
    }

    [Test]
    public async Task GroundedSpawn_RejectsUnavailableSurface()
    {
        var source = new PositionAndRotation(Vector3.Zero, Vector3.Zero);

        var resolved = MateSpawnPositionResolver.TryResolve(
            source,
            0f,
            3f,
            true,
            _ => null,
            out _);

        await Assert.That(resolved).IsFalse();
    }

    [Test]
    public async Task GroundedSpawn_RejectsSurfaceOnDifferentLayer()
    {
        var source = new PositionAndRotation(
            new Vector3(10f, 20f, 90f),
            Vector3.Zero);

        var resolved = MateSpawnPositionResolver.TryResolve(
            source,
            0f,
            3f,
            true,
            _ => 10f,
            out _);

        await Assert.That(resolved).IsFalse();
    }

    [Test]
    public async Task NonGroundedSpawn_PreservesHeightWithoutSamplingTerrain()
    {
        var source = new PositionAndRotation(
            new Vector3(10f, 20f, 42f),
            Vector3.Zero);
        var sampleCount = 0;

        var resolved = MateSpawnPositionResolver.TryResolve(
            source,
            0f,
            3f,
            false,
            _ =>
            {
                sampleCount++;
                return 0f;
            },
            out var spawnPosition);

        await Assert.That(resolved).IsTrue();
        await Assert.That(sampleCount).IsEqualTo(0);
        await Assert.That(spawnPosition.Position.Z).IsEqualTo(42f);
    }

    [Test]
    [Arguments(0, false, true)]
    [Arguments(1, false, true)]
    [Arguments(1, true, false)]
    [Arguments(2, false, false)]
    [Arguments(3, false, false)]
    public async Task RequiresGrounding_UsesMovementAndUnderwaterMetadata(
        int movementId,
        bool underwaterCreature,
        bool expected)
    {
        var actorModel = new ActorModel
        {
            MovementId = movementId,
            UnderwaterCreature = underwaterCreature
        };

        var result = MateSpawnPositionResolver.RequiresGrounding(actorModel);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task RequiresGrounding_MissingModelDefaultsToSafeGroundPlacement()
    {
        var result = MateSpawnPositionResolver.RequiresGrounding(null);

        await Assert.That(result).IsTrue();
    }
}
