using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Scripting;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Delayed world-script action (e.g. the bridge slimes spawning only after cinematic Nerta's
/// sequence finishes). Holds the world, not the controller: after instance teardown the world's
/// collections are empty and the action no-ops. aaemu-cluster#92.
/// </summary>
public class WorldScriptActionTask(WorldInstance world, WorldScriptAction action) : Task
{
    public override void Execute()
    {
        WorldScriptController.ExecuteAction(world, action);
    }
}
