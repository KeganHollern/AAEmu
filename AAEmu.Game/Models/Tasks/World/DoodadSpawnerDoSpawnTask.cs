using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.Game.Models.Tasks.World;

public class DoodadSpawnerDoSpawnTask(DoodadSpawner doodadSpawner) : Task
{
    public override void Execute()
    {
        if (doodadSpawner.Last == null)
            doodadSpawner.Spawn(0);
        else
            doodadSpawner.DoSpawn();
    }
}
