using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Scripting;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// Delayed world-script skill cast, so a scripted sequence can start after its NPC was
/// event-spawned (spawning happens on the next world tick after Activate()).
/// aaemu-cluster#92: retail escort sequences hang off skills carrying RunCommandSet.
/// </summary>
public class WorldScriptCastSkillTask(WorldInstance world, WorldScriptCastSkill cast) : Task
{
    public override void Execute()
    {
        WorldScriptController.CastNow(world, cast);
    }
}
