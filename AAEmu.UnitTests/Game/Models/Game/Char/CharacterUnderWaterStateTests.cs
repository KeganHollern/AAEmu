using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.World;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

/// <summary>
/// aaemu-cluster#92: the Sharpwind pit flood (22 m over 22 s) is a doodad-phase animation the
/// client only RENDERS — its physics volume stays at the phase-start extent until the next phase
/// spawns the risen prefab (cuttingwind.water1 → water2 at +22 m), so players physically cannot
/// swim while the water rises. The server must therefore keep the underwater/breath state on the
/// phase-start (gameplay) surface: round 1's 1 Hz stepped surface flapped SCUnderWaterPacket and
/// bounced players; round 2 found that even a continuous ANIMATED surface declares a floor-walker
/// underwater in water the client does not have yet (single sticky SCUnderWaterPacket(true) +
/// breath drain = residual bob, no swim). These tests drive the real WaterBodies animation through
/// the real Character.SetPosition composition (TryGetGameplaySurface + GetIsUnderWaterState) at
/// movement-packet rate.
/// </summary>
public class CharacterUnderWaterStateTests
{
    /// <summary>Movement-packet sampling interval (ms).</summary>
    private const long PacketMs = 50;

    private static bool Probe(WaterBodies water, Vector3 probe, bool wasUnderWater)
    {
        // Exactly the Character.SetPosition composition.
        return water.TryGetGameplaySurface(probe, out var gameplaySurface) &&
               Character.GetIsUnderWaterState(wasUnderWater, probe.Z, gameplaySurface);
    }

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

    /// <summary>
    /// The player-reported scenario: standing on the pit floor at Z=144 while the surface animates
    /// 145 → 167. The animated enter threshold (surface − 2) sweeps past the feet at surface 146
    /// (t ≈ 1 s) and the exit threshold (surface − 1.65) can never be re-crossed while the surface
    /// rises — on the round-1 build that was one sticky SCUnderWaterPacket(true) plus breath drain
    /// for the remaining 21 s. Against the gameplay (phase-start) surface the player stays dry for
    /// the entire rise and flips underwater exactly once when the animation completes — the same
    /// moment the client gains the risen, swimmable volume from the next doodad phase.
    /// </summary>
    [Test]
    public async Task FloorStander_DuringRise_StaysDry_ThenEntersOnceAtCompletion()
    {
        var time = new FakeTimeProvider();
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = time };
        var area = water.AddSquareArea("SharpwindPit", new Vector3(100f, 100f, 145f), 70f, 2f);
        water.AnimateAreaSurface(area.Id, 22f, 22f);

        var probe = new Vector3(100f, 100f, 144f); // stationary feet on the pit floor
        var underWater = false;
        var transitions = 0;
        var midRiseAnimatedSurface = 0f;
        var midRiseGameplaySurface = 0f;

        for (var ms = 0L; ms < 22_000; ms += PacketMs)
        {
            var next = Probe(water, probe, underWater);
            if (next != underWater)
                transitions++;
            underWater = next;

            if (ms == 11_000)
            {
                midRiseAnimatedSurface = water.GetWaterSurface(probe, out _);
                water.TryGetGameplaySurface(probe, out midRiseGameplaySurface);
            }

            time.Advance(TimeSpan.FromMilliseconds(PacketMs));
        }

        // Whole rise: zero SCUnderWaterPacket traffic, no breath drain, nothing fights walking.
        await Assert.That(transitions).IsEqualTo(0);
        await Assert.That(underWater).IsFalse();

        // Mid-rise the animated (ship/fall-damage) surface was ~11 m up while the gameplay
        // (client physics) surface stayed at the start level.
        await Assert.That(midRiseAnimatedSurface).IsEqualTo(156f).Within(0.01f);
        await Assert.That(midRiseGameplaySurface).IsEqualTo(145f).Within(0.01f);

        // Animation complete (clock is at 22 s): the gameplay surface snaps to the final level in
        // the same tick the next doodad phase gives the client its swimmable volume -> the
        // floor-stander flips underwater exactly once, and swimming/breath work normally.
        await Assert.That(water.TryGetGameplaySurface(probe, out var finalSurface)).IsTrue();
        await Assert.That(finalSurface).IsEqualTo(167f).Within(0.01f);
        await Assert.That(Probe(water, probe, underWater)).IsTrue();
    }

    /// <summary>
    /// A player wading through the original shallow pool while the surface animates overhead must
    /// see zero underwater transitions: feet bobbing anywhere inside the phase-start band
    /// [bottom 143, start surface 145] never cross the gameplay enter threshold (143).
    /// </summary>
    [Test]
    public async Task WadingPlayer_DuringRise_SeesZeroUnderwaterTransitions()
    {
        var time = new FakeTimeProvider();
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = time };
        var area = water.AddSquareArea("SharpwindPit", new Vector3(100f, 100f, 145f), 70f, 2f);
        water.AnimateAreaSurface(area.Id, 22f, 22f);

        var underWater = false;
        var transitions = 0;

        for (var ms = 0L; ms < 22_000; ms += PacketMs)
        {
            var feetZ = 144f + 0.5f * MathF.Sin(ms / 500f); // uneven floor / walking bob
            var next = Probe(water, new Vector3(100f, 100f, feetZ), underWater);
            if (next != underWater)
                transitions++;
            underWater = next;

            time.Advance(TimeSpan.FromMilliseconds(PacketMs));
        }

        await Assert.That(transitions).IsEqualTo(0);
        await Assert.That(underWater).IsFalse();
    }

    /// <summary>
    /// Drain counterpart (Howling Abyss boss pools drain up to 26 m): the client keeps its HIGH
    /// physics volume until the drained phase spawns, so a deep swimmer must keep breath pressure
    /// for the whole drain — forcing the state off while an animation runs would hand out free
    /// breath. Exit happens exactly once, when the drain completes.
    /// </summary>
    [Test]
    public async Task DeepSwimmer_DuringDrain_KeepsUnderwaterState_UntilDrainCompletes()
    {
        var time = new FakeTimeProvider();
        var water = new WaterBodies { OceanLevel = 0f, AnimationTimeProvider = time };
        var area = water.AddSquareArea("BossPool", new Vector3(100f, 100f, 167f), 70f, 24f);
        water.AnimateAreaSurface(area.Id, -22f, 22f);

        var probe = new Vector3(100f, 100f, 160f); // 7 m under the pre-drain surface
        var underWater = true;                     // already submerged when the drain starts
        var transitions = 0;

        for (var ms = 0L; ms < 22_000; ms += PacketMs)
        {
            var next = Probe(water, probe, underWater);
            if (next != underWater)
                transitions++;
            underWater = next;

            time.Advance(TimeSpan.FromMilliseconds(PacketMs));
        }

        // Whole drain: still underwater (breath keeps draining) even though the animated surface
        // dropped below the swimmer long ago.
        await Assert.That(transitions).IsEqualTo(0);
        await Assert.That(underWater).IsTrue();

        // Drain complete: the swimmer is above the final surface -> exits exactly once.
        await Assert.That(Probe(water, probe, underWater)).IsFalse();
    }
}
