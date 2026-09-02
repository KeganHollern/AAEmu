using System.Numerics;

using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects;

public class SpawnEffectTests
{
    [Test]
    public async Task ResolveSlaveSpawnPosition_RentalZeroDistancePreservesPlayerPosition()
    {
        var sourcePosition = new Vector3(10f, 20f, 30f);
        var sourceRotation = new Vector3(0.1f, 0.2f, 0.35f);
        var source = new PositionAndRotation(sourcePosition, sourceRotation);
        var effect = new SpawnEffect
        {
            PosDirId = 1,
            PosDistance = 0f,
            OriDirId = 3,
            OriAngle = 90f
        };

        var spawnPosition = effect.ResolveSlaveSpawnPosition(source);

        await Assert.That(spawnPosition.Position).IsEqualTo(sourcePosition);
        await Assert.That(spawnPosition.Rotation.X).IsEqualTo(sourceRotation.X).Within(0.001f);
        await Assert.That(spawnPosition.Rotation.Y).IsEqualTo(sourceRotation.Y).Within(0.001f);
        await Assert.That(spawnPosition.Rotation.Z).IsEqualTo(sourceRotation.Z + MathF.PI / 2f).Within(0.001f);
        await Assert.That(effect.PosDistance).IsEqualTo(0f);
        await Assert.That(source.Position).IsEqualTo(sourcePosition);
        await Assert.That(source.Rotation).IsEqualTo(sourceRotation);
    }

    [Test]
    public async Task ResolveSlaveSpawnPosition_OtherZeroDistanceKeepsTwoMeterFallback()
    {
        var sourcePosition = new Vector3(10f, 20f, 30f);
        var sourceRotation = new Vector3(0f, 0f, 0f);
        var source = new PositionAndRotation(sourcePosition, sourceRotation);
        var effect = new SpawnEffect
        {
            PosDirId = 2,
            PosDistance = 0f,
            OriDirId = 2,
            OriAngle = 0f
        };

        var spawnPosition = effect.ResolveSlaveSpawnPosition(source);

        await Assert.That(spawnPosition.Position).IsEqualTo(new Vector3(10f, 22f, 30f));
        await Assert.That(spawnPosition.Rotation).IsEqualTo(sourceRotation);
        await Assert.That(effect.PosDistance).IsEqualTo(0f);
        await Assert.That(source.Position).IsEqualTo(sourcePosition);
        await Assert.That(source.Rotation).IsEqualTo(sourceRotation);
    }
}
