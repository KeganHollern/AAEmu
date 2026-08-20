using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.World;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

/// <summary>
/// aaemu-cluster#92: while the Sharpwind pit floods (22 m over 22 s) a swimming player must keep a
/// stable underwater state. The old 1 s stepped server surface jumped past the 0.35 m hysteresis in
/// Character.SetPosition every step, flipping SCUnderWaterPacket at ~1 Hz and bouncing the
/// character between swim and fall. These tests drive the real WaterBodies surface animation
/// through the real band decision at movement-packet rate.
/// </summary>
public class CharacterUnderWaterStateTests
{
    [Test]
    public async Task GetIsUnderWaterState_HysteresisBand_IsSticky()
    {
        const float surface = 100f;

        // Dry -> enters only below surface - 2
        await Assert.That(Character.GetIsUnderWaterState(false, 98.1f, surface)).IsFalse();
        await Assert.That(Character.GetIsUnderWaterState(false, 97.9f, surface)).IsTrue();

        // Wet -> exits only above surface - 1.65
        await Assert.That(Character.GetIsUnderWaterState(true, 98.2f, surface)).IsTrue();
        await Assert.That(Character.GetIsUnderWaterState(true, 98.4f, surface)).IsFalse();
    }

    [Test]
    public async Task SurfaceSwimmer_DuringContinuousSharpwindRise_NeverFlipsUnderwaterState()
    {
        var time = new FakeTimeProvider();
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = time };
        var area = water.AddSquareArea("SharpwindPit", new Vector3(100f, 100f, 100f), 70f, 2f);
        water.AnimateAreaSurface(area.Id, 22f, 22f);

        const float floorZ = 100f;        // pit floor == initial surface
        const float swimFeetDepth = 1.5f; // the client keeps a swimmer's feet ~1.5 m under its own surface

        var underWater = false;
        var transitions = 0;

        // The client lerps its own water volume continuously and floats the character on it,
        // trailing the server clock by ~80 ms of latency. Sample at movement-packet rate (50 ms).
        for (var ms = 0L; ms <= 22_000; ms += 50)
        {
            var clientSurface = 100f + 22f * Math.Clamp((ms - 80) / 22_000f, 0f, 1f);
            var feetZ = MathF.Max(floorZ, clientSurface - swimFeetDepth);
            var probe = new Vector3(100f, 100f, feetZ);

            var next = water.IsWater(probe, out _) &&
                       Character.GetIsUnderWaterState(underWater, feetZ, water.GetWaterSurface(probe, out _));
            if (next != underWater)
                transitions++;
            underWater = next;

            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        // A surface swimmer's feet stay ~1.5 m deep — above the 2 m enter threshold for the whole
        // rise, so SCUnderWaterPacket is never sent and nothing fights the client's swim.
        await Assert.That(transitions).IsEqualTo(0);
        await Assert.That(underWater).IsFalse();
    }

    [Test]
    public async Task FloorStander_DuringContinuousRise_EntersUnderwaterExactlyOnce()
    {
        var time = new FakeTimeProvider();
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = time };
        var area = water.AddSquareArea("SharpwindPit", new Vector3(100f, 100f, 100f), 70f, 2f);
        water.AnimateAreaSurface(area.Id, 22f, 22f);

        const float feetZ = 100f; // refuses to swim, stays on the pit floor
        var probe = new Vector3(100f, 100f, feetZ);
        var underWater = false;
        var transitions = 0;

        for (var ms = 0L; ms <= 22_000; ms += 50)
        {
            var next = water.IsWater(probe, out _) &&
                       Character.GetIsUnderWaterState(underWater, feetZ, water.GetWaterSurface(probe, out _));
            if (next != underWater)
                transitions++;
            underWater = next;

            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        // Breath bookkeeping still works: one clean enter once the head is under, no flapping.
        await Assert.That(transitions).IsEqualTo(1);
        await Assert.That(underWater).IsTrue();
    }
}
