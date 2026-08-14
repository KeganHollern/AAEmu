using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Tasks.Mate;

public sealed class TemporaryMateDespawnTask(MateManager manager, Character owner, Game.Units.Mate mate) : Task
{
    public override void Execute()
    {
        manager.RemoveActiveMateAndDespawn(owner, mate);
    }
}
