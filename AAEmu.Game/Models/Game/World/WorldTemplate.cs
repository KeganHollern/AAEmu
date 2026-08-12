using System.Collections.Concurrent;
using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Xml;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Utils;
using NLog;

namespace AAEmu.Game.Models.Game.World;

/// <summary>
/// Template of a World
/// </summary>
public class WorldTemplate
{
    private static Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// TemplateId for this world
    /// </summary>
    public uint Id { get; set; }

    /// <summary>
    /// World name
    /// </summary>
    public virtual string Name { get; set; }

    /// <summary>
    /// Max height for this world's map data
    /// </summary>
    public float MaxHeight { get; set; }

    /// <summary>
    /// Height Coefficient
    /// </summary>
    public virtual double HeightMaxCoefficient { get; set; }
    /// <summary>
    /// Height of the ocean surface for this world
    /// </summary>
    public float OceanLevel { get; set; } = 100f;
    /// <summary>
    /// World X size in Cells (1024m)
    /// </summary>
    public int CellX { get; set; }
    /// <summary>
    /// World Y size in Cells (1024m)
    /// </summary>
    public int CellY { get; set; }
    /// <summary>
    /// Default spawn location for this world (not used when creating new characters)
    /// </summary>
    public WorldSpawnPosition SpawnPosition { get; set; } = new();

    public WorldCell[,] Cells { get; set; } = new WorldCell[1, 1];
    // <summary>
    // Raw Heightmap data for this world
    // </summary>
    // public virtual ushort[,] HeightMaps { get; set; }

    // <summary>
    // List of what cells have been loaded/processed
    // </summary>
    // public virtual bool[,] LoadedCells { get; set; }

    /// <summary>
    /// Collection of ZoneKeys per Region
    /// </summary>
    public uint[,] ZoneKeyByRegions { get; set; }
    
    /// <summary>
    /// List of levels inside this world (Zone Keys)
    /// </summary>
    public List<uint> ZoneKeys { get; set; } = [];

    /// <summary>
    /// Xml data for this world
    /// </summary>
    public XmlWorld XmlWorld { get; set; } = new();

    /// <summary>
    /// XML Zone data (zoneKey, data)
    /// </summary>
    public ConcurrentDictionary<uint, XmlWorldZone> XmlWorldZones { get; set; } = [];

    /// <summary>
    /// List of SubZones in this world (zoneId, list)
    /// </summary>
    public Dictionary<uint, List<Area>> SubZones { get; set; } = [];
    /// <summary>
    /// List of housing zones in this world (zoneId, list)
    /// </summary>
    public Dictionary<uint, List<Area>> HousingZones { get; set; } = []; 

    /// <summary>
    /// Handles navmesh data
    /// </summary>
    public AiGeoDataManager GeoData { get; set; }

    /// <summary>
    /// ZoneKey, BaiLoader
    /// </summary>
    public Dictionary<uint, BaseBaiLoader> ZoneBaiLoader { get; init; } = [];
    /// <summary>
    /// (PathX, PathY), BaiLoader
    /// </summary>
    public ConcurrentDictionary<(uint, uint), BaseBaiLoader> PathBaiLoader { get; init; } = new();

    /// <summary>
    /// Gets heightmap height at target position (not smoothened)
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public float GetRawHeightMapHeight(int x, int y)
    {
        var cellX = x / WorldManager.CELL_SIZE;
        var cellY = y / WorldManager.CELL_SIZE;
        if (cellX < 0 || cellX > CellX || cellY < 0 || cellY > CellY)
            return 0f; // out of bounds
        var cell = Cells[cellX, cellY].VerifyCellLoaded();
        var sx = x % WorldManager.CELL_SIZE / 2;
        var sy = y % WorldManager.CELL_SIZE / 2;
        return (float)(cell.HeightMap[sx, sy] / HeightMaxCoefficient);
    }

    /// <summary>
    /// Picks the nearest 4 points of a square that contain target position
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    private static System.Drawing.Rectangle FindNearestSignificantPoints(int x, int y)
    {
        return new System.Drawing.Rectangle(x - x % 2, y - y % 2, 2, 2);
    }

    /// <summary>
    /// Gets height at target position using the terrain mesh's triangle interpolation
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public float GetHeight(float x, float y)
    {
        // return GetRawHeightMapHeight((int)x, (int)y); // <-- the old way we used to do things

        // Get bordering points
        var border = FindNearestSignificantPoints((int)Math.Floor(x), (int)Math.Floor(y));

        // Get heights for these points
        var heightX0Y0 = GetRawHeightMapHeight(border.Left, border.Top);
        var heightX1Y0 = GetRawHeightMapHeight(border.Right, border.Top);
        var heightX0Y1 = GetRawHeightMapHeight(border.Left, border.Bottom);
        var heightX1Y1 = GetRawHeightMapHeight(border.Right, border.Bottom);
        var offX = (x - border.Left) / 2;
        var offY = (y - border.Top) / 2;

        // CryEngine renders each heightmap square as two triangles split between X1Y0 and X0Y1.
        // Interpolate on that same surface so the server agrees with the terrain visible to clients.
        if (offX + offY < 1f)
            return heightX0Y0 * (1f - offX - offY) + heightX1Y0 * offX + heightX0Y1 * offY;

        return heightX1Y1 * (offX + offY - 1f) + heightX0Y1 * (1f - offX) + heightX1Y0 * (1f - offY);
    }

    /// <summary>
    /// Checks if target sector offset is within the world's bounds
    /// </summary>
    /// <param name="sectorX"></param>
    /// <param name="sectorY"></param>
    /// <returns></returns>
    public bool ValidRegion(int sectorX, int sectorY)
    {
        return sectorX >= 0 && sectorX < CellX * WorldManager.SECTORS_PER_CELL && sectorY >= 0 && sectorY < CellY * WorldManager.SECTORS_PER_CELL;
    }

    /// <summary>
    /// Gets target cell
    /// </summary>
    /// <param name="cellX"></param>
    /// <param name="cellY"></param>
    /// <returns>Returns the cell, or null if the given index is out of bounds for this world</returns>
    public WorldCell GetCell(int cellX, int cellY)
    {
        if (cellX < 0 || cellX > CellX || cellY < 0 || cellY > CellY)
            return null;
        return Cells[cellX, cellY];
    }

    public void LoadZoneBaiFiles()
    {
        if (!AppConfiguration.Instance.World.GeoDataMode)
            return; // Don't load navmesh if GeoDataMode is disabled

        foreach (var zoneKey in ZoneKeys)
        {
            var worldFolder = Path.Combine("game", "worlds", Name, "zone", zoneKey.ToString());
            var baiFilesList = ClientFileManager.GetFilesInDirectory(worldFolder, "*.bai", false).ToArray();
            if (baiFilesList.Length <= 0)
                continue;

            var zoneBaiLoader = new BaseBaiLoader(this);
            zoneBaiLoader.LoadBaiFilesFromFolder(zoneKey.ToString());
            ZoneBaiLoader.Add(zoneKey, zoneBaiLoader);
        }
    }

    public BaseBaiLoader GetBaiByPos(Vector3 pos)
    {
        if (ZoneBaiLoader.Count > 0)
            return ZoneBaiLoader.Values.First(); // TODO: Pick the actually correct zone

        // First verify if target cell is loaded
        var cellPos = pos.ToCellIndex();
        var cell = Cells[cellPos.Item1, cellPos.Item2];
        cell.VerifyCellLoaded();
        // Return value from the main paths dictionary
        var pathsPos = pos.ToPathsIndex();
        return PathBaiLoader.GetValueOrDefault(((uint)pathsPos.Item1, (uint)pathsPos.Item2));
    }
}
