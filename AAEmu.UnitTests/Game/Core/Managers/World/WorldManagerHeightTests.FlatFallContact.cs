using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Units;
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
        await Assert.That(ground.HasBrushFallSupport(new(109, 100, DeckZ))).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(new(110.75f, 100, DeckZ))).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(113, 100, DeckZ))).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(new(109, 100, DeckZ - 0.01f))).IsFalse();
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
        await Assert.That(ground.HasBrushFallSupport(foot)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot)).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(foot)).IsTrue();
        await Assert.That(tries).IsEqualTo(2);

        var animated = new AiGeoDataManager(template)
        {
            GroundBrushGeometry = brush => read(brush) with { HasAnimatedCollision = true }
        };
        await Assert.That(animated.HasBrushFallSupport(foot)).IsFalse();
    }

    [Test]
    [Arguments(0.5f, 0f, false, true)]
    [Arguments(-0.5f, 0f, false, true)]
    [Arguments(0f, 1f, false, true)]
    [Arguments(0f, 1.25f, false, false)] // 51.34 degrees, steeper than the stock fall angle.
    [Arguments(0.5f, 0f, true, false)] // The same inclined triangle faces down.
    public async Task PlayerFallContact_UsesTheActualInclinedFaceAndVerticalTolerance(
        float riseX, float riseY, bool underside, bool supported)
    {
        Vector3 Point(float x, float y) => new(x, y, DeckZ + (x - 110) * riseX + (y - 100) * riseY);
        var mesh = new CryTriangleMesh([Point(108, 98), Point(112, 98), Point(110, 102)],
            underside ? [0, 2, 1] : [0, 1, 2], []);
        var ground = CreateDeckWalker(customMesh: mesh).ParentWorld.Template.GeoData;
        var foot = Point(110, 100);
        var tolerance = FallMovement.PositionTolerance;
        var dx = MathF.BitDecrement(foot.X + 1f / 512f) - foot.X;
        var dy = MathF.BitDecrement(foot.Y + 1f / 512f) - foot.Y;
        var upperBound = tolerance + MathF.Max(0, riseX * dx) + MathF.Max(0, riseY * dy);
        var lowerBound = tolerance - MathF.Min(0, riseX * dx) - MathF.Min(0, riseY * dy);
        var above = foot.Z + upperBound;
        if (above - foot.Z > upperBound)
            above = MathF.BitDecrement(above);
        var below = foot.Z - lowerBound;
        if (foot.Z - below > lowerBound)
            below = MathF.BitIncrement(below);

        await Assert.That(ground.HasBrushFallSupport(foot, true)).IsEqualTo(supported);
        await Assert.That(ground.HasBrushFallSupport(new(foot.X, foot.Y, above), true)).IsEqualTo(supported);
        await Assert.That(ground.HasBrushFallSupport(new(foot.X, foot.Y, below), true)).IsEqualTo(supported);
        await Assert.That(ground.HasBrushFallSupport(new(foot.X, foot.Y, MathF.BitIncrement(above)), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(foot.X, foot.Y, MathF.BitDecrement(below)), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(Point(111.5f, 101), true)).IsFalse(); // Outside the actual triangle.
        await Assert.That(ground.HasBrushFallSupport(foot, false)).IsFalse();
    }

    [Test]
    public async Task PlayerFallContact_HorizontalRoundingCannotExtendTheTriangleAtItsUpperEdge()
    {
        Vector3 Point(float x, float y) => new(x, y, DeckZ + x - 110);
        var mesh = new CryTriangleMesh([Point(108, 98), Point(110, 98), Point(110, 102)], [0, 1, 2], []);
        var ground = CreateDeckWalker(customMesh: mesh).ParentWorld.Template.GeoData;
        var beyondVerticalBand = Vector3.UnitZ * (FallMovement.PositionTolerance + 0.001f);
        await Assert.That(ground.HasBrushFallSupport(Point(109.5f, 100) + beyondVerticalBand, true)).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(Point(110, 100), true)).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(Point(110, 100) + beyondVerticalBand, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(Point(MathF.BitIncrement(110), 100), true)).IsFalse();
    }
}
