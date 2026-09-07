using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

public class SpawnDoodadTests
{
    [Test]
    public async Task TryResolvePlacement_OffsetDestination_SamplesFinalWorldPosition()
    {
        var sourcePosition = new Vector3(10f, 20f, 30f);
        Vector3? sampledPosition = null;

        var resolved = SpawnDoodad.TryResolvePlacement(
            sourcePosition,
            0f,
            DynamicDoodadPlacementPolicy.GroundToNearbySurface,
            (Vector3 position, out GroundSurfaceResult surface) =>
            {
                sampledPosition = position;
                surface = new GroundSurfaceResult(28f, GroundSurfaceSource.Terrain,
                    GroundSurfaceDecision.TerrainOnly, GroundSurfaceFailure.None, null);
                return true;
            },
            out var placementPosition);

        await Assert.That(resolved).IsTrue();
        await Assert.That(sampledPosition).IsNotNull();
        await Assert.That(sampledPosition!.Value).IsEqualTo(new Vector3(10f, 21f, 30f));
        await Assert.That(placementPosition).IsEqualTo(new Vector3(10f, 21f, 28f));
    }
}
