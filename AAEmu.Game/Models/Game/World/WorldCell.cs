using System.Collections;
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
    private TerrainHeightSector[] _terrain;
    private float[] _nativeEdgeJunctions;
    internal bool HasNativeTerrain => _nativeEdgeJunctions != null;
    internal bool HasHeightMap => _terrain != null || HeightMap != null;
    private BitArray[] _terrainHoles;
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

        // Logger.Debug($"Loading {heightMapFile}");

        // Read the cell hmap data
        using var br = new BinaryReader(stream);
        var hmap = new Hmap();

        if (hmap.Read(br, true) < 0 || !TryReadTerrainHeightMap(hmap))
        {
            Logger.Error($"Error reading {heightMapFile}");
            return false;
        }

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

    internal bool TryReadTerrainHeightMap(Hmap hmap)
    {
        // Sort nodes by position
        var sortedNodes = hmap.Nodes
            .OrderBy(cell => cell.BoxHeightmap.Min.X)
            .ThenBy(cell => cell.BoxHeightmap.Min.Y)
            .Where(x => x.pHMData.Length > 0)
            .ToList();

        const int sectorUnits = WorldManager.SECTOR_HMAP_RESOLUTION;
        const int sectorCount = WorldManager.SECTORS_PER_CELL;
        const int sectorMetres = WorldManager.REGION_SIZE;
        if (sortedNodes.Count != sectorCount * sectorCount)
            return false;
        // The file origin can be nonzero. Each of the 256 leaf slots must occupy its own square.
        var origin = hmap.Nodes[0].pHMData.Length == 0 ? hmap.Nodes[0].BoxHeightmap.Min : sortedNodes[0].BoxHeightmap.Min;
        for (var i = 0; i < sortedNodes.Count; i++)
        {
            var box = sortedNodes[i].BoxHeightmap;
            var x = origin.X + i / sectorCount * sectorMetres;
            var y = origin.Y + i % sectorCount * sectorMetres;
            if (box.Min.X != x || box.Min.Y != y || box.Max.X != x + sectorMetres || box.Max.Y != y + sectorMetres)
                return false;
        }
        var holes = sortedNodes.Any(node => node.TerrainHoles != null)
            ? sortedNodes.Select(node => node.TerrainHoles).ToArray() : null;

        var minimum = float.MaxValue;
        var maximum = 0f;
        var nativeFormat = hmap.Version == 24 && (hmap.Flags & 1) == 0 && hmap.UnitSizeInMeters == 2 &&
            hmap.SectorSizeInMeters == sectorMetres && hmap.SectorsTableSizeInSectors >= sectorCount &&
            hmap.HeightMapSizeInUnits == (long)hmap.SectorsTableSizeInSectors * sectorUnits &&
            sortedNodes.All(TerrainHeightSector.SupportsNativeNode);
        var junctions = nativeFormat ? new float[2 * (sectorCount - 1)] : null;
        if (nativeFormat)
        {
            // At a junction on the outer cell edge neither of its two sectors owns the vertex.
            // The native grid keeps the first one in file order. Save those 30 original corners
            // before converting the node arrays for the legacy heightmap.
            for (var i = 1; i < sectorCount; i++)
            {
                var east = (sectorCount - 1) * sectorCount + i;
                var north = i * sectorCount + sectorCount - 1;
                junctions[i - 1] = ReadEdgeCorner(sortedNodes[east - 1], sortedNodes[east], true);
                junctions[sectorCount - 1 + i - 1] =
                    ReadEdgeCorner(sortedNodes[north - sectorCount], sortedNodes[north], false);
            }
        }

        float ReadEdgeCorner(NodeCell previous, NodeCell next, bool east)
        {
            var first = hmap.Nodes.IndexOf(previous) < hmap.Nodes.IndexOf(next) ? previous : next;
            var offset = first.pHMData.Length - 1;
            if (first == next)
                offset = east ? (first.nSize - 1) * first.nSize : first.nSize - 1;
            return first.fOffset + (first.pHMData[offset] & 0xffe0) * first.fRange;
        }

        var terrain = new TerrainHeightSector[sectorCount * sectorCount];
        for (var sx = 0; sx < sectorCount; sx++)
        for (var sy = 0; sy < sectorCount; sy++)
        {
            var index = sx * sectorCount + sy;
            var width = sectorUnits + (sx == sectorCount - 1 ? 1 : 0);
            var height = sectorUnits + (sy == sectorCount - 1 ? 1 : 0);
            terrain[index] = new TerrainHeightSector(sortedNodes[index], Template.HeightMaxCoefficient, width, height, nativeFormat,
                out var low, out var high);
            minimum = MathF.Min(minimum, low);
            maximum = MathF.Max(maximum, high);
        }

        _terrainHoles = holes;
        _nativeEdgeJunctions = junctions;
        _terrain = terrain;
        MinHeight = minimum;
        MaxHeight = maximum;
        BoundingBox = new JBoundingBox(new JVector(CellOffset.X, MinHeight, CellOffset.Y),
            new JVector(CellOffset.X + WorldManager.CELL_SIZE, MaxHeight, CellOffset.Y + WorldManager.CELL_SIZE));
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

    internal bool IsTerrainHole(float worldX, float worldY)
    {
        if (_terrainHoles == null)
            return false;

        // The owning square at a retained outer edge is the last square in this cell.
        const int unitCount = WorldManager.CELL_HMAP_RESOLUTION;
        const int sectorUnits = WorldManager.SECTOR_HMAP_RESOLUTION;
        var x = Math.Clamp(((int)worldX - CellX * WorldManager.CELL_SIZE) / 2, 0, unitCount - 1);
        var y = Math.Clamp(((int)worldY - CellY * WorldManager.CELL_SIZE) / 2, 0, unitCount - 1);
        var sector = _terrainHoles[x / sectorUnits * WorldManager.SECTORS_PER_CELL + y / sectorUnits];
        return sector?[x % sectorUnits * sectorUnits + y % sectorUnits] == true;
    }

    internal bool TryGetHeightMapSample(int x, int y, out float height, bool native = false)
    {
        height = 0f;
        if (!Loaded || !double.IsFinite(Template.HeightMaxCoefficient) || Template.HeightMaxCoefficient <= 0d)
            return false;
        float sample;
        if (_terrain != null)
        {
            if ((uint)x > WorldManager.CELL_HMAP_RESOLUTION || (uint)y > WorldManager.CELL_HMAP_RESOLUTION)
                return false;
            const int units = WorldManager.SECTOR_HMAP_RESOLUTION;
            const int sectors = WorldManager.SECTORS_PER_CELL;
            const int last = WorldManager.CELL_HMAP_RESOLUTION;
            if (native && _nativeEdgeJunctions != null)
            {
                if (x == last && y > 0 && y < last && y % units == 0)
                {
                    height = _nativeEdgeJunctions[y / units - 1];
                    return float.IsFinite(height);
                }
                if (y == last && x > 0 && x < last && x % units == 0)
                {
                    height = _nativeEdgeJunctions[sectors - 1 + x / units - 1];
                    return float.IsFinite(height);
                }
            }
            var sx = Math.Min(x / units, sectors - 1);
            var sy = Math.Min(y / units, sectors - 1);
            var section = _terrain[sx * sectors + sy];
            var localX = x - sx * units;
            var localY = y - sy * units;
            sample = native && HasNativeTerrain ? section.NativeHeight(localX, localY)
                : (float)(section.LegacyHeight(localX, localY, Template.HeightMaxCoefficient) / Template.HeightMaxCoefficient);
        }
        else
        {
            if (HeightMap == null || (uint)x >= HeightMap.GetLength(0) || (uint)y >= HeightMap.GetLength(1))
                return false;
            sample = (float)(HeightMap[x, y] / Template.HeightMaxCoefficient);
        }
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
