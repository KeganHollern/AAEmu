using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using CgfConverter.Structs;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FallContact_MirroredPlacementPreservesTheMeshGapAndTheUpwardSide(bool mirrored)
    {
        var npc = CreateDeckWalker(hasGap: true);
        var template = npc.ParentWorld.Template;
        var brush = (ObjectDataType1Brush)template.Cells[0, 0].LoadedObjectDat.PrefabsList.Single();
        if (mirrored)
            brush.Matrix3X4 = new Matrix3x4 { M11 = -1, M22 = 1, M33 = 1, M14 = 221.5f };
        var ground = template.GeoData;
        await Assert.That(ground.HasFlatFallSupport(new(109, 100, DeckZ))).IsTrue();
        await Assert.That(ground.HasFlatFallSupport(new(110.75f, 100, DeckZ))).IsFalse();
        await Assert.That(ground.HasFlatFallSupport(new(113, 100, DeckZ))).IsTrue();
        await Assert.That(ground.HasFlatFallSupport(new(109, 100, DeckZ - 0.01f))).IsFalse();
    }

    [Test]
    public async Task FallContact_RetriesAReportedReadFailureAndSkipsAnimatedGeometry()
    {
        var npc = CreateDeckWalker();
        var template = npc.ParentWorld.Template;
        var read = template.GeoData.GroundBrushGeometry;
        var tries = 0;
        var ground = new AiGeoDataManager(template)
        {
            GroundBrushGeometry = brush => ++tries == 1 ? throw new IOException("Temporary read failure") : read(brush)
        };
        var foot = new Vector3(109, 100, DeckZ);
        await Assert.That(ground.HasFlatFallSupport(foot)).IsFalse();
        await Assert.That(ground.HasFlatFallSupport(foot)).IsTrue();
        await Assert.That(ground.HasFlatFallSupport(foot)).IsTrue();
        await Assert.That(tries).IsEqualTo(2);

        var animated = new AiGeoDataManager(template)
        {
            GroundBrushGeometry = brush => read(brush) with { HasAnimatedCollision = true }
        };
        await Assert.That(animated.HasFlatFallSupport(foot)).IsFalse();
    }
}
