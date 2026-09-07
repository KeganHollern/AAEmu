using NLog;
using DotNetTask = System.Threading.Tasks.Task;
using Task = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.Game.Models.Game.Indun;

public class DungeonLoaderTask(Dungeon dungeon) : Task
{
    // ReSharper disable once InconsistentNaming
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public override void Execute()
    {
        ExecuteAsync().GetAwaiter().GetResult();
    }

    public override async DotNetTask ExecuteAsync()
    {
        // The constructor creates the world before scheduling this task. A missing world
        // here belongs to a dungeon that has already been torn down; do not recreate it.
        var world = dungeon.World;
        if (world == null || dungeon.IsDestroyed || dungeon.FinishedLoading)
            return;

        try
        {
            Logger.Debug($"[{world}] Spawning dungeon game objects...");
            world.SpawnManager.SpawnAll();
            await DotNetTask.WhenAll(world.SpawnManager.SpawnTasks).ConfigureAwait(false);
            Logger.Debug($"[{world}] Finished spawning dungeon game objects.");

            dungeon.CompleteLoading(world);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, $"[{world}] Dungeon loading failed.");
            throw;
        }
    }
}
