using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Scripting;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Delayed delivery of a world-script NPC line, so a beat can land after its NPC
/// was event-spawned (spawning happens on the next world tick after Activate()).
/// aaemu-cluster#92 validation: silent Allistair/Nerta beats.
/// </summary>
public class WorldScriptSayTask(WorldInstance world, WorldScriptSay say) : Task
{
    public override void Execute()
    {
        WorldScriptController.SayNow(world, say);
    }
}
