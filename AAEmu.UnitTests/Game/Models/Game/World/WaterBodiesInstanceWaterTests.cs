using System.Numerics;

using AAEmu.Game.Models.Game.World;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.World;

/// <summary>
/// Covers the instance-world water ingest threshold, the DoodadFuncWaterVolume raise API and the
/// continuous surface animation (aaemu-cluster#92 / #93 / #98).
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
    public async Task AnimateAreaSurface_InterpolatesContinuously_ByElapsedTime()
    {
        var time = new FakeTimeProvider();
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = time };
        var area = water.AddSquareArea("Pit", new Vector3(100f, 100f, 100f), 70f, 2f);
        var probe = new Vector3(100f, 100f, 100f);

        // The Sharpwind flood: +22 m over 22 s (DoodadFuncWaterVolume LevelChange/Duration).
        await Assert.That(water.AnimateAreaSurface(area.Id, 22f, 22f)).IsTrue();

        // No time elapsed -> surface unchanged
        await Assert.That(water.GetWaterSurface(probe, out _)).IsEqualTo(100f).Within(0.001f);

        time.Advance(TimeSpan.FromSeconds(5.5)); // 25 %
        await Assert.That(water.GetWaterSurface(probe, out _)).IsEqualTo(105.5f).Within(0.01f);

        time.Advance(TimeSpan.FromSeconds(5.5)); // 50 %
        await Assert.That(water.GetWaterSurface(probe, out _)).IsEqualTo(111f).Within(0.01f);

        time.Advance(TimeSpan.FromSeconds(19)); // past the end -> clamped at 100 %
        await Assert.That(water.GetWaterSurface(probe, out _)).IsEqualTo(122f).Within(0.01f);

        time.Advance(TimeSpan.FromSeconds(30)); // the finished animation is dropped, the surface stays
        await Assert.That(water.GetWaterSurface(probe, out _)).IsEqualTo(122f).Within(0.01f);
    }

    [Test]
    public async Task AnimateAreaSurface_MidRise_KeepsOriginalBottomWet()
    {
        var time = new FakeTimeProvider();
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = time };
        var area = water.AddSquareArea("Pit", new Vector3(100f, 100f, 100f), 70f, 2f);
        water.AnimateAreaSurface(area.Id, 22f, 22f);

        time.Advance(TimeSpan.FromSeconds(11)); // 50 %
        // The original band (surface 100, depth 2) stays wet halfway through the rise...
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 98.5f), out _)).IsTrue();
        // ...the newly flooded column is wet as well...
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 110f), out _)).IsTrue();
        // ...but not above the interpolated surface (111 at 50 %)
        await Assert.That(water.IsWater(new Vector3(100f, 100f, 112f), out _)).IsFalse();
    }

    [Test]
    public async Task AnimateAreaSurface_NonPositiveDuration_AppliesImmediately()
    {
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = new FakeTimeProvider() };
        var area = water.AddSquareArea("Pit", new Vector3(100f, 100f, 100f), 70f, 2f);

        await Assert.That(water.AnimateAreaSurface(area.Id, 3f, 0f)).IsTrue();
        await Assert.That(water.GetWaterSurface(new Vector3(100f, 100f, 100f), out _)).IsEqualTo(103f).Within(0.001f);
    }

    [Test]
    public async Task AnimateAreaSurface_UnknownArea_ReturnsFalse()
    {
        var water = new WaterBodies { OceanLevel = 0f };

        await Assert.That(water.AnimateAreaSurface(42, 5f, 10f)).IsFalse();
    }
}
