using AAEmu.Game.Models.Game.World;

using NLog;

namespace AAEmu.Game.Models.Tasks.World;

/// <summary>
/// One ~1s step of a DoodadFuncWaterVolume surface animation; scheduled repeating by
/// TaskManager with the step count computed from the func's Duration. aaemu-cluster#92 / #98.
/// </summary>
public class WaterSurfaceChangeTask(WorldInstance world, uint areaId, float stepDeltaZ) : Task
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override void Execute()
    {
        if (!world.Water.RaiseAreaSurface(areaId, stepDeltaZ))
            Logger.Warn($"WaterSurfaceChangeTask: water area {areaId} not found in {world}");
    }
}
