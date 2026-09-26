using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class SubZoneManager(IWorldManager worldManager, IZoneManager zoneManager) : Singleton<SubZoneManager>, ISubZoneManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public void Load()
    {
        #region LoadClientData

        var worldTemplates = worldManager.GetAllWorldTemplates();
        if (worldTemplates == null || worldTemplates.Length == 0)
        {
            return;
        }
        foreach (var worldTemplate in worldTemplates)
        {
            worldTemplate.SubZones.Clear();
            worldTemplate.HousingZones.Clear();
            var zonesList = worldManager.GetZoneKeysByWorldId(worldTemplate.Id);

            foreach (var zoneKey in zonesList)
            {
                var zone = zoneManager.GetZoneByKey(zoneKey);
                if (zone is null)
                {
                    // Not done loading, or conflicting zone data?
                    var zoneName = string.Empty;
                    if (worldTemplate.XmlWorldZones.TryGetValue(zoneKey, out var xmlZone))
                    {
                        zoneName = xmlZone.Name;
                    }
                    Logger.Debug($"XML ZoneKey {zoneKey} ({zoneName}) in {worldTemplate.Name} does not exist in database (unused area)");
                    continue;
                }
                #region subzone

                var worldLevelDesignDir = Path.Combine("game", "worlds", worldTemplate.Name, "level_design", "zone", zone.ZoneKey.ToString(), "client");
                var pathFiles = ClientFileManager.GetFilesInDirectory(worldLevelDesignDir, "subzone_area.xml", true);

                if (!worldTemplate.XmlWorldZones.TryGetValue(zone.ZoneKey, out var subzoneXmlZone))
                    throw new InvalidDataException($"Subzone geometry references missing XML zone {zone.ZoneKey}.");
                worldTemplate.SubZones[zone.Id] = Area.ReadSubZones(pathFiles,
                    ClientFileManager.GetFileAsString, subzoneXmlZone);

                #endregion subzone

                #region housing_area

                worldLevelDesignDir = Path.Combine("game", "worlds", worldTemplate.Name, "level_design", "zone", zone.ZoneKey.ToString(), "client");
                pathFiles = ClientFileManager.GetFilesInDirectory(worldLevelDesignDir, "housing_area.xml", true);

                if (!worldTemplate.XmlWorldZones.TryGetValue(zone.ZoneKey, out var housingXmlZone))
                    throw new InvalidDataException($"Housing geometry references missing XML zone {zone.ZoneKey}.");
                worldTemplate.HousingZones[zone.ZoneKey] = HousingAreaPolygon.ReadSources(pathFiles,
                    ClientFileManager.GetFileAsString, housingXmlZone).ToList();

                #endregion housing_area
            }
            Logger.Info("Loaded {0} housing polygons for {1}.",
                worldTemplate.HousingZones.Values.Sum(polygons => polygons.Count), worldTemplate.Name);
        }

        #endregion
    }

    public List<uint> GetHousingZoneByPosition(WorldInstance world, float x, float y)
    {
        // A polygon can cross a terrain zone boundary. Search the whole world.
        return world?.Template.HousingZones.Values.SelectMany(polygons => polygons)
            .Where(polygon => polygon.Contains2D(x, y)).Select(polygon => polygon.Id).Distinct().ToList() ?? [];
    }

    public List<uint> GetSubZoneByPosition(WorldTemplate worldTemplate, Vector3 pos)
    {
        if (worldTemplate == null || !float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
            return [];
        return worldTemplate.SubZones.Values.SelectMany(areas => areas)
            .Where(area => area.Id != 0 && area.Contains(pos))
            .Select(area => area.Id).Distinct().Order().ToList();
    }

    public List<uint> GetSubZoneByPosition(WorldTemplate worldTemplate, float x, float y)
    {
        if (worldTemplate == null || !float.IsFinite(x) || !float.IsFinite(y))
            return [];

        // Authored polygons can cross terrain-zone boundaries. Keep the current world's geometry authoritative.
        var position = new Vector3(x, y, 0);
        return worldTemplate.SubZones.Values.SelectMany(areas => areas)
            .Where(area => area.Id != 0 && Point.IsInside(area.Points, area.Points.Count, position))
            .Select(area => area.Id).Distinct().Order().ToList();
    }

}
