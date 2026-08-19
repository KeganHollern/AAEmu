using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class BubbleEffect : EffectTemplate
{
    /// <summary>
    /// Reading pace used to size a bubble's on-screen time, from the 2012 study cited below:
    /// 900 characters/minute is 15 characters/second, i.e. 66.7ms per character.
    /// https://iovs.arvojournals.org/article.aspx?articleid=2166061
    /// </summary>
    private const double MillisecondsPerCharacter = 1000.0 / 15.0;

    /// <summary>Floor for very short lines ("Right here!"), and the value used when no text resolves.</summary>
    private const double MinimumDisplayMs = 1250;

    /// <summary>Ceiling so one long line cannot monopolise a speaker for an absurd time.</summary>
    private const double MaximumDisplayMs = 8000;

    public uint KindId { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        Logger.Trace($"BubbleEffect, Id {Id}, KindId {KindId}, ObjId {targetObj.ObjId}");

        if (target == null)
            return;

        // Text is not sent: the id lets each client render the line in its own locale.
        var displayMs = ResolveDisplayMs(LocalizationManager.Instance.Get("bubble_effects", "speech", Id, string.Empty));

        // aaemu-cluster#92: this used to Thread.Sleep(displayMs) to pace consecutive bubbles. Effects
        // are applied synchronously from AIManager.Tick, which walks every NpcAi in the server inside
        // a lock and whose tick is skipped while the previous one is still running — so a single
        // spoken line froze all NPC AI (and NPC spawn/despawn registration) for over a second, and
        // silently stretched every scripted dialogue beat.
        //
        // Stagger only the bubbles of ONE cast: those would otherwise all land in the same instant
        // and the client would show just the last. Bubbles from SEPARATE casts must never delay each
        // other — their spacing belongs to the caller (ai_commands Timeout), and deferring one by the
        // previous line's reading time would push a scripted line past its own beat, even past the
        // speaker's despawn.
        var castTlId = castObj is CastSkill castSkill ? castSkill.TlId : (ushort)0;
        var delay = ReserveSlot(target, castTlId, displayMs);

        if (delay <= TimeSpan.Zero)
        {
            target.BroadcastPacket(new SCChatBubblePacket(targetObj.ObjId, (byte)KindId, 2, Id, string.Empty), true);
            return;
        }

        TaskManager.Instance.Schedule(
            new Tasks.Units.ChatBubbleTask(target, targetObj.ObjId, (byte)KindId, Id), delay);
    }

    /// <summary>How long the client should be left showing this line before the next sibling bubble.</summary>
    internal static double ResolveDisplayMs(string localizedText)
    {
        return string.IsNullOrEmpty(localizedText)
            ? MinimumDisplayMs
            : Math.Clamp(localizedText.Length * MillisecondsPerCharacter, MinimumDisplayMs, MaximumDisplayMs);
    }

    /// <summary>
    /// Returns how long this bubble waits before being sent, and advances the speaker's bookkeeping.
    /// Within one cast (same TlId) siblings queue up behind each other; a new cast — or a source with
    /// no cast identity — always sends immediately, so a scripted line never slips past its beat.
    /// </summary>
    internal static TimeSpan ReserveSlot(BaseUnit speaker, ushort castTlId, double displayMs)
    {
        if (castTlId == 0 || speaker.BubbleCastTlId != castTlId)
        {
            speaker.BubbleCastTlId = castTlId;
            speaker.BubbleCastOffset = TimeSpan.Zero;
        }

        var delay = speaker.BubbleCastOffset;
        speaker.BubbleCastOffset += TimeSpan.FromMilliseconds(displayMs);
        return delay;
    }
}
