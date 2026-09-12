using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.PublicFarm;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class PublicFarmManager(ITaskManager taskManager, IWorldManager worldManager, ISubZoneManager subZoneManager) : Singleton<PublicFarmManager>, IPublicFarmManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public void Initialize()
    {
        Logger.Info("Initialising Public Farm Manager...");
        PublicFarmTickStart();
    }

    private void PublicFarmTickStart()
    {
        Logger.Info("PublicFarmTickTask: Started");

        var lpTickStartTask = new PublicFarmTickStartTask();
        taskManager.Schedule(lpTickStartTask, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void PublicFarmTick()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            PublicFarmTickLocked(DateTime.UtcNow);
        }
    }

    private void PublicFarmTickLocked(DateTime now)
    {
        // NOTE: Public farms only available in main_world
        var world = worldManager.GetWorld(WorldManager.DefaultInstanceId);
        var deleted = new List<Doodad>();
        foreach (var doodad in world?.SpawnManager?.GetAllPlayerDoodads().ToArray() ?? [])
        {
            if (doodad is null)
                continue;
            if (doodad.FarmType == FarmType.Invalid) { continue; }
            if (IsProtected(doodad, now)) { continue; }

            // defense time is up
            doodad.OwnerId = 0;
            doodad.OwnerType = DoodadOwnerType.System;
            doodad.FarmType = FarmType.Invalid;
            doodad.Save();
            deleted.Add(doodad);
        }

        foreach (var doodad in deleted)
        {
            //doodad.Delete();
            world.SpawnManager?.RemovePlayerDoodad(doodad);
        }
    }

    public bool InPublicFarm(WorldTemplate worldTemplate, Vector3 pos)
    {
        var subZoneList = subZoneManager.GetSubZoneByPosition(worldTemplate, pos);
        return subZoneList.Any(subZoneId => CommonFarmGameData.Instance.GetSubzoneFarmGroup(subZoneId) != FarmType.Invalid);
    }

    private uint GetFarmId(WorldInstance world, Vector3 pos)
    {
        var subZoneList = subZoneManager.GetSubZoneByPosition(world.Template, pos);

        return subZoneList.FirstOrDefault(subZoneId => CommonFarmGameData.Instance.GetSubzoneFarmGroup(subZoneId) != FarmType.Invalid);
    }

    public FarmType GetFarmType(WorldInstance world, Vector3 pos)
    {
        var subZoneId = GetFarmId(world, pos);
        return CommonFarmGameData.Instance.GetSubzoneFarmGroup(subZoneId);
    }

    /// <summary>
    /// Checks if a given doodad can be placed on a given farm type.
    /// Checks for type and max count
    /// </summary>
    /// <param name="character"></param>
    /// <param name="farmType"></param>
    /// <param name="doodadId"></param>
    /// <returns></returns>
    public bool CanPlace(Character character, FarmType farmType, uint doodadId)
    {
        var allPlanted = GetCommonFarmDoodads(character);
        if (allPlanted.TryGetValue(farmType, out var doodadList))
        {
            if (doodadList.Count >= CommonFarmGameData.Instance.GetFarmGroupMaxCount(farmType))
            {
                character.SendErrorMessage(Models.Game.ErrorMessageType.CommonFarmCountOver);
                return false;
            }
        }

        var allowedDoodads = CommonFarmGameData.Instance.GetAllowedDoodads(farmType);
        if (allowedDoodads.Any(id => doodadId == id))
        {
            return true;
        }

        character.SendErrorMessage(Models.Game.ErrorMessageType.CommonFarmNotAllowedType);
        return false;
    }

    public Dictionary<FarmType, List<Doodad>> GetCommonFarmDoodads(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            return GetCommonFarmDoodadsLocked(character, DateTime.UtcNow);
        }
    }

    private Dictionary<FarmType, List<Doodad>> GetCommonFarmDoodadsLocked(Character character, DateTime now)
    {
        var list = new Dictionary<FarmType, List<Doodad>>();

        var playerDoodads = character.ParentWorld?.SpawnManager?.GetPlayerDoodads(character.Id) ?? [];

        foreach (var doodad in playerDoodads)
        {
            if (InPublicFarm(character.ParentWorld.Template, doodad.Transform.World.Position))
            {
                var farmType = GetFarmType(character.ParentWorld, doodad.Transform.World.Position);

                if (doodad.FarmType == farmType && IsProtected(doodad, now))
                {
                    if (!list.ContainsKey(farmType))
                        list.Add(farmType, []);
                    list[farmType].Add(doodad);
                }
            }
        }

        return list;
    }

    public static bool IsProtected(Doodad doodad) => IsProtected(doodad, DateTime.UtcNow);

    public static bool IsProtected(Doodad doodad, DateTime now)
    {
        if (doodad == null || doodad.OwnerId == 0 || doodad.FarmType == FarmType.Invalid)
            return false;
        return IsWithinProtection(doodad.PlantTime, now,
            CommonFarmGameData.Instance.GetGuardTimeMilliseconds(doodad.FarmType));
    }

    public static bool IsWithinProtection(DateTime planted, DateTime now, uint guardTimeMilliseconds) =>
        guardTimeMilliseconds > 0 && now - planted < TimeSpan.FromMilliseconds(guardTimeMilliseconds);

    public void Load() { }

}
