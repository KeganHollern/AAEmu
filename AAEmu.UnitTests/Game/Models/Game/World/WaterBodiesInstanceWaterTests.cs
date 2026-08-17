using System.Numerics;

using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World;

/// <summary>
/// Covers the instance-world water ingest threshold, the DoodadFuncWaterVolume raise API and its
/// animation step math (aaemu-cluster#92 / #93 / #98).
/// </summary>
public class WaterBodiesInstanceWaterTests
{
    /// <summary>The Sharpwind Mines lake quad footprint (m²) that the old global 5000 m² cut dropped.</summary>
    private const float SharpwindLakeBboxSqm = 4420f;

    [Test]
    public async Task GetMinIngestBboxAreaSqm_MainWorld_KeepsDecorativePuddleCut()
    {
        await Assert.That(WaterBodies.GetMinIngestBboxAreaSqm(true))
            .IsEqualTo(WaterBodies.MinWaterBboxAreaSquareMeters);
        // main_world still drops the dungeon-lake-sized footprint (that cut is why the constant exists)
        await Assert.That(SharpwindLakeBboxSqm < WaterBodies.GetMinIngestBboxAreaSqm(true)).IsTrue();
    }

    [Test]
    public async Task GetMinIngestBboxAreaSqm_InstanceWorld_KeepsDungeonPools()
    {
        await Assert.That(WaterBodies.GetMinIngestBboxAreaSqm(false))
            .IsEqualTo(WaterBodies.MinInstanceWaterBboxAreaSquareMeters);
        // the Sharpwind Mines lake quad must survive instance ingest
        await Assert.That(SharpwindLakeBboxSqm >= WaterBodies.GetMinIngestBboxAreaSqm(false)).IsTrue();
    }

    [Test]
    public async Task AddSquareArea_CreatesWaterBand_FromSurfaceDownToDepth()
    {
        var water = new WaterBodies { OceanLevel = 0f };
        var center = new Vector3(100f, 100f, 100f);

        var area = water.AddSquareArea("TestPool", center, 70f, 2f);

        await Assert.That(area).IsNotNull();
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 99f), out _)).IsTrue();  // inside the band
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 97f), out _)).IsFalse(); // below the bottom
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 101f), out _)).IsFalse(); // above the surface
        await Assert.That(water.IsWater(new Vector3(150f, 100f, 99f), out _)).IsFalse(); // outside the square
    }

    [Test]
    public async Task RaiseAreaSurface_RaisesSurface_AndKeepsOriginalBottomWet()
    {
        var water = new WaterBodies { OceanLevel = 0f };
        var area = water.AddSquareArea("TestPool", new Vector3(100f, 100f, 100f), 70f, 2f);

        var raised = water.RaiseAreaSurface(area.Id, 5f);

        await Assert.That(raised).IsTrue();
        var surface = water.GetWaterSurface(new Vector3(100f, 100f, 104f), out _);
        await Assert.That(surface).IsEqualTo(105f).Within(0.001f);
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 104f), out _)).IsTrue();  // new band
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 99f), out _)).IsTrue();   // original band stays wet
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 106f), out _)).IsFalse(); // above the new surface
    }

    [Test]
    public async Task RaiseAreaSurface_UnknownArea_ReturnsFalse()
    {
        var water = new WaterBodies { OceanLevel = 0f };

        await Assert.That(water.RaiseAreaSurface(42, 5f)).IsFalse();
    }

    [Test]
    public async Task GetNearestArea_PrefersContainingArea_AndHonorsMaxDistance()
    {
        var water = new WaterBodies { OceanLevel = 0f };
        var pool = water.AddSquareArea("Pool", new Vector3(100f, 100f, 100f), 70f, 2f);

        // Inside the bbox -> distance 0
        var inside = water.GetNearestArea(new Vector3(100f, 100f, 100f), 100f);
        await Assert.That(inside).IsNotNull();
        await Assert.That(inside.Id).IsEqualTo(pool.Id);

        // Outside the bbox (edge at x=135) but within reach
        var near = water.GetNearestArea(new Vector3(200f, 100f, 100f), 100f);
        await Assert.That(near).IsNotNull();

        // Beyond maxDistance
        var far = water.GetNearestArea(new Vector3(300f, 100f, 100f), 100f);
        await Assert.That(far).IsNull();
    }

    [Test]
    public async Task WaterVolumeAnimation_StepMath_CoversDurationAndSumsToLevelChange()
    {
        await Assert.That(DoodadFuncWaterVolume.GetAnimationStepCount(30f)).IsEqualTo(30);
        await Assert.That(DoodadFuncWaterVolume.GetAnimationStepCount(0.5f)).IsEqualTo(1);
        await Assert.That(DoodadFuncWaterVolume.GetAnimationStepCount(0f)).IsEqualTo(1);
        await Assert.That(DoodadFuncWaterVolume.GetAnimationStepCount(float.NaN)).IsEqualTo(1);

        var steps = DoodadFuncWaterVolume.GetAnimationStepCount(10f);
        var delta = DoodadFuncWaterVolume.GetAnimationStepDelta(3.5f, steps);
        await Assert.That(delta * steps).IsEqualTo(3.5f).Within(0.0001f);

        // Draining (negative LevelChange) keeps its sign
        await Assert.That(DoodadFuncWaterVolume.GetAnimationStepDelta(-2f, 4)).IsEqualTo(-0.5f).Within(0.0001f);
    }
}
