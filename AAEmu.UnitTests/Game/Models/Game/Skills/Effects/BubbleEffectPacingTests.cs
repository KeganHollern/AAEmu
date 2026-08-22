using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects;

/// <summary>
/// Guards how NPC chat bubbles are paced (aaemu-cluster#92).
///
/// BubbleEffect.Apply used to Thread.Sleep for the line's display time. Effects are applied
/// synchronously from AIManager.Tick, which walks every NpcAi in the server inside a lock and whose
/// tick is skipped while the previous one is still running, so one spoken line stalled all NPC AI —
/// and it silently stretched every scripted dialogue beat past its authored length.
///
/// The replacement staggers siblings of a single cast and must NEVER defer a bubble belonging to a
/// later cast: scripted sequences pace themselves with ai_commands Timeout, and a line pushed behind
/// the previous line's reading time can land after its own beat, even after the speaker despawned.
/// </summary>
public class BubbleEffectPacingTests
{
    private const double MinimumDisplayMs = 1250;

    private static BaseUnit CreateSpeaker() => new();

    [Test]
    public async Task SeparateCastsNeverDelayEachOther()
    {
        var speaker = CreateSpeaker();
        // Sharpwind set 185: one cast per line (TlId 12, 13, 14 in the live logs), each authored 1s
        // apart. Bubble 2148 is long ("The thieves' hideout is down this mine shaft…"), so a
        // cross-cast reservation would have deferred the next line by ~6s.
        var longLine = new string('x', 88);

        var first = BubbleEffect.ReserveSlot(speaker, 12, BubbleEffect.ResolveDisplayMs("Right here!"));
        var second = BubbleEffect.ReserveSlot(speaker, 13, BubbleEffect.ResolveDisplayMs(longLine));
        var third = BubbleEffect.ReserveSlot(speaker, 14, BubbleEffect.ResolveDisplayMs("Here we go!"));

        await Assert.That(first).IsEqualTo(TimeSpan.Zero);
        await Assert.That(second).IsEqualTo(TimeSpan.Zero);
        await Assert.That(third).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task SiblingBubblesOfOneCastQueueBehindEachOther()
    {
        var speaker = CreateSpeaker();
        // Skill 20308 carries 24 BubbleEffects; without staggering the client only shows the last.
        var display = BubbleEffect.ResolveDisplayMs("Right here!");

        var first = BubbleEffect.ReserveSlot(speaker, 77, display);
        var second = BubbleEffect.ReserveSlot(speaker, 77, display);
        var third = BubbleEffect.ReserveSlot(speaker, 77, display);

        await Assert.That(first).IsEqualTo(TimeSpan.Zero);
        await Assert.That(second).IsEqualTo(TimeSpan.FromMilliseconds(display));
        await Assert.That(third).IsEqualTo(TimeSpan.FromMilliseconds(display * 2));
    }

    [Test]
    public async Task SourcesWithoutACastIdentityAlwaysSendImmediately()
    {
        var speaker = CreateSpeaker();
        // AreaTrigger and BuffTemplate apply effects with no CastSkill, so there is no cast to group.
        var first = BubbleEffect.ReserveSlot(speaker, 0, MinimumDisplayMs);
        var second = BubbleEffect.ReserveSlot(speaker, 0, MinimumDisplayMs);

        await Assert.That(first).IsEqualTo(TimeSpan.Zero);
        await Assert.That(second).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task DisplayTimeFollowsReadingSpeedWithinBounds()
    {
        // The old code computed length * 0.015 — seconds-per-character used as milliseconds — so a
        // 90-character line resolved to 1ms and the 1250ms floor always won, making the cited
        // reading-speed estimate dead code. 900 chars/minute is 66.7ms per character.
        var shortLine = BubbleEffect.ResolveDisplayMs("Right here!");
        var mediumLine = BubbleEffect.ResolveDisplayMs(new string('x', 90));
        var hugeLine = BubbleEffect.ResolveDisplayMs(new string('x', 5000));
        var missing = BubbleEffect.ResolveDisplayMs(string.Empty);

        await Assert.That(shortLine).IsEqualTo(MinimumDisplayMs);          // clamped up
        await Assert.That(mediumLine).IsGreaterThan(5000d);                // ~6000ms, not 1ms
        await Assert.That(mediumLine).IsLessThan(7000d);
        await Assert.That(hugeLine).IsEqualTo(8000d);                      // clamped down
        await Assert.That(missing).IsEqualTo(MinimumDisplayMs);
    }
}
