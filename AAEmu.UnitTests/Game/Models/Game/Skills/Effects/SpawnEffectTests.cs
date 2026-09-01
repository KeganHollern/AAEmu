using System.Numerics;

using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects;

public class SpawnEffectTests
{
    [Test]
    public async Task ResolveSlaveSpawnPosition_ZeroDistancePreservesAuthoredPositionAndTemplate()
    {
        const float sourceYaw = 0.35f;
        var sourcePosition = new Vector3(10f, 20f, 30f);
        var sourceRotation = new Vector3(0f, 0f, sourceYaw);
        var source = new PositionAndRotation(sourcePosition, sourceRotation);
        var effect = new SpawnEffect
        {
            PosDistance = 0f,
            OriAngle = 90f
        };

        var spawnPosition = effect.ResolveSlaveSpawnPosition(source);

        await Assert.That(spawnPosition.Position).IsEqualTo(sourcePosition);
        await Assert.That(spawnPosition.Rotation.Z).IsEqualTo(sourceYaw + MathF.PI / 2f).Within(0.001f);
        await Assert.That(effect.PosDistance).IsEqualTo(0f);
        await Assert.That(source.Position).IsEqualTo(sourcePosition);
        await Assert.That(source.Rotation).IsEqualTo(sourceRotation);
    }
}
