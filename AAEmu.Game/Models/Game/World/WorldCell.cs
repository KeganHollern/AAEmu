using System.Numerics;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.ClientData;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Objects;
using Jitter2.LinearMath;
using NLog;

namespace AAEmu.Game.Models.Game.World;

public class WorldCell
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public WorldTemplate Template { get; init; }
    public int CellX { get; init; }
    public int CellY { get; init; }
    private volatile bool _loaded;
    public bool Loaded { get => _loaded; private set => _loaded = value; }
    private readonly Lock _loadLock = new();
    private Vector3 CellOffset { get; set; }
    internal ushort[,] HeightMap { get; private set; }
    private float MinHeight { get; set; }
    private float MaxHeight { get; set; }

    /// <summary>
    /// Bai files data to use in this cell
    /// </summary>
    public BaseBaiLoader[,] BaiLoader { get; set; }

    /// <summary>
    /// Bounding box for use in Jitter
    /// </summary>
    public JBoundingBox BoundingBox { get; private set; }

    /// <summary>Prefab tree from cell <c>client/object.dat</c> (CryEngine).</summary>
    public ObjectsFile LoadedObjectDat { get; set; }

    public WorldCell(int cellX, int cellY, WorldTemplate template)
    {
        CellX = cellX;
        CellY = cellY;
        Template = template;
        CellOffset = new Vector3(CellX * WorldManager.CELL_SIZE, CellY * WorldManager.CELL_SIZE, 0f);
        // Default bounding box
        BoundingBox = new JBoundingBox(
            new JVector(CellOffset.X, 0f, CellOffset.Y), 
            new JVector(CellOffset.X + WorldManager.CELL_SIZE, 0f, CellOffset.Y + WorldManager.CELL_SIZE)
            );
        BaiLoader = new BaseBaiLoader[4, 4]
        {
            { null, null, null, null },
            { null, null, null, null },
            { null, null, null, null },
            { null, null, null, null }
        };
    }

    /// <summary>
    /// Load the *.bai files for this Cell if GeoDataMode is enabled 
    /// </summary>
    private void LoadBaiFiles()
    {
        if (!AppConfiguration.Instance.World.GeoDataMode)
            return; // Don't load navmesh if GeoDataMode is disabled

        // If we already loaded zone bai data for this world template, then don't try to load the cell one
        // Instead reference the zone bai directly
        if (Template.ZoneBaiLoader.Count > 0)
        {
            // This is zone grabbing is just an estimate, but should be good enough for this purpose
            // Ideally you'd check all 256 sectors in the cell and take it's median, but feels like overkill here
            var zoneKey = Template.ZoneKeyByRegions[CellX * WorldManager.SECTORS_PER_CELL, CellY * WorldManager.SECTORS_PER_CELL];
            if (Template.ZoneBaiLoader.TryGetValue(zoneKey, out var parentZoneBaiLoader))
            {
                // Assign it for all paths in this cell
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        BaiLoader[x, y] = parentZoneBaiLoader;
                    }
                }
            }
            else
            {
                Logger.Warn($"WorldTemplate {Template.Name} has Zone bai files data loaded ({Template.ZoneBaiLoader.Count} zones), but could not find a matching file for this cell {zoneKey}");
            }
            return;
        }

        // Load the 4x4 grid of path folders into this cell
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var pathX = (uint)(CellX * 4 + x);
                var pathY = (uint)(CellY * 4 + y);
                var pathFolder = $"{pathX:000}_{pathY:000}";
                var pathBaiLoader = new BaseBaiLoader(Template);
                pathBaiLoader.LoadBaiFilesFromFolder(pathFolder); // (x != 0 || y != 0)
                BaiLoader[x, y] = pathBaiLoader;
                if (!Template.PathBaiLoader.TryAdd((pathX, pathY), pathBaiLoader))
                    Logger.Warn($"PathBaiLoader already contains key ({pathX}, {pathY}) for template {Template.Name}");
            }
        }
    }

    /// <summary>
    /// Checks if the cell is loaded and loads it if it hasn't 
    /// </summary>
    /// <returns></returns>
    public WorldCell VerifyCellLoaded()
    {
        if (Loaded)
            return this;

        lock (_loadLock)
        {
            if (Loaded)
                return this;

            LoadBaiFiles();
            Loaded = LoadCellHeightMapFromClientData();
        }
        return this;
    }

    /// <summary>
    /// Loads a given Cell worth of heightmap data
    /// </summary>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    private bool LoadCellHeightMapFromClientData()
    {
        var cellFileName = $"{CellX:000}_{CellY:000}";
        var heightMapFile = Path.Combine("game", "worlds", Template.Name, "cells", cellFileName, "client", "terrain", "heightmap.dat");
        if (!ClientFileManager.FileExists(heightMapFile))
        {
            return true;
        }

        using var stream = ClientFileManager.GetFileStream(heightMapFile);
        if (stream == null)
        {
            return true;
        }

        HeightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION + 1, WorldManager.CELL_HMAP_RESOLUTION + 1];

        // Logger.Debug($"Loading {heightMapFile}");

        // Read the cell hmap data
        using var br = new BinaryReader(stream);
        var hmap = new Hmap();

        if (hmap.Read(br, false) < 0)
        {
            Logger.Error($"Error reading {heightMapFile}");
            return false;
        }

        // Sort nodes by position
        var sortedNodes = hmap.Nodes
            .OrderBy(cell => cell.BoxHeightmap.Min.X)
            .ThenBy(cell => cell.BoxHeightmap.Min.Y)
            .Where(x => x.pHMData.Length > 0)
            .ToList();

        // Read nodes into heightmap array
        #region ReadNodes

        MinHeight = float.MaxValue;
        MaxHeight = 0f;
        // The last sector includes the endpoint of the final triangle in the cell.
        const int sectorUnits = WorldManager.SECTOR_HMAP_RESOLUTION;
        const int sectorCount = WorldManager.SECTORS_PER_CELL;
        for (var x = 0; x <= WorldManager.CELL_HMAP_RESOLUTION; x++)
        {
            var sectorX = Math.Min(x / sectorUnits, sectorCount - 1);
            var unitX = (ushort)(x - sectorX * sectorUnits);
            for (var y = 0; y <= WorldManager.CELL_HMAP_RESOLUTION; y++)
            {
                var sectorY = Math.Min(y / sectorUnits, sectorCount - 1);
                var unitY = (ushort)(y - sectorY * sectorUnits);
                var node = sortedNodes[sectorX * sectorCount + sectorY];
                var value = (ushort)(node.GetHeightAtUnit(unitX, unitY) * Template.HeightMaxCoefficient);
                HeightMap[x, y] = value;
                var height = (float)(value / Template.HeightMaxCoefficient);
                MinHeight = MathF.Min(height, MinHeight);
                MaxHeight = MathF.Max(height, MaxHeight);
            }
        }
        #endregion

        // Update bounding box
        BoundingBox = new JBoundingBox(
            new JVector(CellOffset.X, MinHeight, CellOffset.Y), 
            new JVector(CellOffset.X + WorldManager.CELL_SIZE, MaxHeight, CellOffset.Y + WorldManager.CELL_SIZE)
        );

        var cellFolder = Path.Combine("game", "worlds", Template.Name, "cells", cellFileName);

        var objectDatFile = Path.Combine(cellFolder, "client", "object.dat");
        if (ClientFileManager.FileExists(objectDatFile))
        {
            var objects = new ObjectsFile(objectDatFile);
            if (objects.ReadFile())
                LoadedObjectDat = objects;
            else
            {
                LoadedObjectDat = objects;
                if (objects.AssetPathsList.Count > 0 || objects.PrefabsList.Count > 0)
                    Logger.Error($"Error loading objects from {objectDatFile}, only {objects.AssetPathsList.Count} assets and {objects.PrefabsList.Count} prefabs read");
            }
        }

        foreach (var worldInstance in WorldManager.Instance.GetWorldsByTemplate(Template.Id))
            worldInstance.Water.AddFromCellData(this);

        return true;
    }

    /// <summary>
    /// Gets heightmap height at target data position, converted to float, but not smoothened
    /// </summary>
    /// <param name="heightMapDataX"></param>
    /// <param name="heightMapDataY"></param>
    /// <returns></returns>
    public float GetHeightMapDataInCell(int heightMapDataX, int heightMapDataY)
    {
        return TryGetHeightMapSample(heightMapDataX, heightMapDataY, out var height) ? height : 0f;
    }

    internal bool TryGetHeightMapSample(int x, int y, out float height)
    {
        height = 0f;
        if (!Loaded || HeightMap == null || (uint)x >= HeightMap.GetLength(0) ||
            (uint)y >= HeightMap.GetLength(1) || !double.IsFinite(Template.HeightMaxCoefficient) ||
            Template.HeightMaxCoefficient <= 0d)
            return false;

        var sample = (float)(HeightMap[x, y] / Template.HeightMaxCoefficient);
        if (!float.IsFinite(sample))
            return false;

        height = sample;
        return true;
    }

    /// <summary>
    /// Gets height at target world position
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns>Returns 0 if it's outside of this cell's bounds, otherwise returns non-smoothened height</returns>
    public float GetHeight(int x, int y)
    {
        var xx = (int)(x - CellOffset.X) / 2;
        var yy = (int)(y - CellOffset.Y) / 2;
        return GetHeightMapDataInCell(xx, yy);
    }

    public Vector3 GetCellWorldOffset() => CellOffset;
}
