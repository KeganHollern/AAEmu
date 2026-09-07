using System.Numerics;
using MySql.Data.MySqlClient;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World.Zones;

namespace AAEmu.Game.Core.Managers.World;

public interface IZoneManager : ILoadable, IInitializable
{
    ZoneConflict[] GetConflicts();
    int Save(MySqlConnection connection, MySqlTransaction transaction);
    Zone GetZoneById(uint zoneId);
    Zone GetZoneByKey(uint zoneKey);
    ZoneGroup GetZoneGroupById(uint zoneId);
    List<uint> GetZoneKeysInZoneGroupById(uint zoneGroupId);
    uint GetTargetIdByZoneId(uint zoneId);
    Vector2 GetZoneOriginCell(uint zoneId);
    Vector3 ConvertToWorldCoordinates(uint zoneId, Vector3 point);
    bool DoodadHasMatchingClimate(Doodad doodad);
    List<Climate> GetClimatesByZone(Zone zone);
}
