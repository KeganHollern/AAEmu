using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Scripting;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Delayed start of a retail ai_command_sets sequence, so it can begin after its NPC was
/// event-spawned (spawning happens on the next world tick after Activate()).
/// aaemu-cluster#92: Sharpwind Mines escort sequences.
/// </summary>
public class WorldScriptCommandSetTask(WorldInstance world, WorldScriptCommandSet run) : Task
{
    public override void Execute()
    {
        WorldScriptController.RunCommandSetNow(world, run);
    }
}
