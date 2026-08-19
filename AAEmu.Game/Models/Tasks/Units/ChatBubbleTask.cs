using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Tasks.Units;

/// <summary>
/// Delayed delivery of one NPC chat bubble. A single cast can carry many BubbleEffects, and they
/// have to appear in sequence; scheduling each one keeps that pacing without blocking a game thread.
/// aaemu-cluster#92: BubbleEffect.Apply used to Thread.Sleep inside AIManager.Tick, which iterates
/// every NpcAi in the server under a lock, so one spoken line stalled all AI for over a second.
/// </summary>
public class ChatBubbleTask(BaseUnit speaker, uint objId, byte kindId, uint bubbleId) : Task
{
    public override void Execute()
    {
        // Unit exposes IsDead; a plain BaseUnit (doodad-like speaker) has no death state.
        if (speaker == null || (speaker is Unit { IsDead: true }))
            return;

        speaker.BroadcastPacket(new SCChatBubblePacket(objId, kindId, 2, bubbleId, string.Empty), true);
    }
}
