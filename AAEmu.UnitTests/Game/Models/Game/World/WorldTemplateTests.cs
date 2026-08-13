using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public class WorldTemplateTests
{
    [Test]
    public async Task GetHeight_LowerTerrainTriangle_DoesNotBlendOppositeCorner()
    {
        var template = CreateWorldTemplate(CreateSingleRaisedCornerHeightMap());

        var height = template.GetHeight(0.5f, 0.5f);

        await Assert.That(height).IsEqualTo(0f).Within(0.001f);
    }

    [Test]
    public async Task GetHeight_UpperTerrainTriangle_MatchesRenderedSurface()
    {
        var template = CreateWorldTemplate(CreateSingleRaisedCornerHeightMap());

        var height = template.GetHeight(1.5f, 1.5f);

        await Assert.That(height).IsEqualTo(50f).Within(0.001f);
    }

    [Test]
    public async Task GetHeight_AtVertexAndSharedDiagonal_IsContinuous()
    {
        var heightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        heightMap[0, 0] = 10;
        heightMap[1, 0] = 30;
        heightMap[0, 1] = 50;
        heightMap[1, 1] = 90;
        var template = CreateWorldTemplate(heightMap);

        await Assert.That(template.GetHeight(0f, 0f)).IsEqualTo(10f).Within(0.001f);
        await Assert.That(template.GetHeight(2f, 2f)).IsEqualTo(90f).Within(0.001f);
        await Assert.That(template.GetHeight(0.6f, 1.4f)).IsEqualTo(44f).Within(0.001f);
    }

    [Test]
    public async Task WorldInstance_GetHeight_UsesTemplateTerrainSurface()
    {
        var template = CreateWorldTemplate(CreateSingleRaisedCornerHeightMap());
        var instance = new WorldInstance(template, 0, true, 1);

        var height = instance.GetHeight(1.5f, 1.5f);

        await Assert.That(height).IsEqualTo(50f).Within(0.001f);
    }

    [Test]
    public async Task AiGeoData_GetHeight_WithoutBai_UsesInterpolatedTerrainSurface()
    {
        var template = CreateWorldTemplate(CreateSingleRaisedCornerHeightMap());
        var geoData = new AiGeoDataManager(template);

        var height = geoData.GetHeight(new Vector3(1.5f, 1.5f, 200f));

        await Assert.That(height).IsEqualTo(50f).Within(0.001f);
    }

    [Test]
    public async Task TryGetGroundHeight_WithoutBai_UsesTerrainSurface()
    {
        var template = CreateWorldTemplate(CreateHeightMap(50));
        var geoData = new AiGeoDataManager(template);

        var found = geoData.TryGetGroundHeight(new Vector3(0.5f, 0.5f, 200f), out var height);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(50f);
    }

    [Test]
    public async Task TryGetGroundHeight_TriangularNavigationNode_UsesTerrainWhileLegacyUsesNode()
    {
        var template = CreateWorldTemplate(CreateHeightMap(50));
        var query = new Vector3(10f, 10f, 90f);
        AddNetMissionBai(template, new Vector3(21.742f, 3.631f, 90f), BaiNavigationType.Triangular);
        var geoData = new AiGeoDataManager(template);

        var legacyFound = geoData.TryGetHeight(query, out var legacyHeight);
        var groundFound = geoData.TryGetGroundHeight(query, out var groundHeight);

        await Assert.That(legacyFound).IsTrue();
        await Assert.That(legacyHeight).IsEqualTo(90f);
        await Assert.That(groundFound).IsTrue();
        await Assert.That(groundHeight).IsEqualTo(50f);
    }

    [Test]
    public async Task TryGetGroundHeight_VertexMissionObstacle_UsesTerrainWhileLegacyUsesObstacle()
    {
        var template = CreateWorldTemplate(CreateHeightMap(50));
        var bai = new BaseBaiLoader(template);
        var vertexMission = new VertexMissionReader(Stream.Null, 1);
        vertexMission.ObstacleDataDescriptorList.Add(new ObstacleDataDescriptor(1)
        {
            Pos = new Vector3(0.5f, 0.5f, 90f)
        });
        bai.VertexMissionReaders.Add(vertexMission);
        template.ZoneBaiLoader.Add(1, bai);
        var geoData = new AiGeoDataManager(template);

        var legacyFound = geoData.TryGetHeight(new Vector3(0.5f, 0.5f, 90f), out var legacyHeight);
        var groundFound = geoData.TryGetGroundHeight(new Vector3(0.5f, 0.5f, 90f), out var groundHeight);

        await Assert.That(legacyFound).IsTrue();
        await Assert.That(legacyHeight).IsEqualTo(90f);
        await Assert.That(groundFound).IsTrue();
        await Assert.That(groundHeight).IsEqualTo(50f);
    }

    [Test]
    public async Task TryGetGroundHeight_WaypointHumanNode_PreservesNavigationHeight()
    {
        var template = CreateWorldTemplate(CreateHeightMap(50));
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.WaypointHuman);
        var geoData = new AiGeoDataManager(template);

        var legacyFound = geoData.TryGetHeight(new Vector3(0.5f, 0.5f, 90f), out var legacyHeight);
        var groundFound = geoData.TryGetGroundHeight(new Vector3(0.5f, 0.5f, 90f), out var groundHeight);

        await Assert.That(legacyFound).IsTrue();
        await Assert.That(legacyHeight).IsEqualTo(90f);
        await Assert.That(groundFound).IsTrue();
        await Assert.That(groundHeight).IsEqualTo(legacyHeight);
    }

    [Test]
    public async Task TryGetGroundHeight_ClosestWaypointHumanNode_PreservesLayeredNavigationHeight()
    {
        var template = CreateWorldTemplate(CreateHeightMap(50));
        var bai = AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 89f), BaiNavigationType.WaypointHuman);
        var netMission = bai.NetMissionReaders[0];
        netMission.NodeDescriptorList.TryAdd(2, new NodeDescriptor(netMission)
        {
            Id = 2,
            Pos = new Vector3(2.5f, 0.5f, 90f),
            NavigationType = BaiNavigationType.Triangular
        });
        var vertexMission = new VertexMissionReader(Stream.Null, 1);
        vertexMission.ObstacleDataDescriptorList.Add(new ObstacleDataDescriptor(1)
        {
            Pos = new Vector3(0.5f, 2f, 90f)
        });
        bai.VertexMissionReaders.Add(vertexMission);
        var geoData = new AiGeoDataManager(template);

        var found = geoData.TryGetGroundHeight(new Vector3(0.5f, 0.5f, 90f), out var height);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(89f);
    }

    [Test]
    public async Task TryGetGroundHeight_TriangularNodeWithoutTerrain_FallsBackToNavigationHeight()
    {
        var template = CreateWorldTemplate(1, 1);
        var cell = new WorldCell(0, 0, template);
        SetPrivateProperty(cell, nameof(WorldCell.Loaded), true);
        template.Cells[0, 0] = cell;
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.Triangular);
        var geoData = new AiGeoDataManager(template);

        var legacyFound = geoData.TryGetHeight(new Vector3(0.5f, 0.5f, 90f), out var legacyHeight);
        var groundFound = geoData.TryGetGroundHeight(new Vector3(0.5f, 0.5f, 90f), out var groundHeight);

        await Assert.That(legacyFound).IsTrue();
        await Assert.That(legacyHeight).IsEqualTo(90f);
        await Assert.That(groundFound).IsTrue();
        await Assert.That(groundHeight).IsEqualTo(legacyHeight);
    }

    [Test]
    public async Task TryGetGroundHeight_TriangularNodeAtSeaLevelTerrain_ReturnsValidZero()
    {
        var template = CreateWorldTemplate(CreateHeightMap());
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.Triangular);
        var geoData = new AiGeoDataManager(template);

        var found = geoData.TryGetGroundHeight(new Vector3(0.5f, 0.5f, 90f), out var height);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(0f);
    }

    [Test]
    public async Task TryGetHeight_FlatSeaLevel_IsAValidSurfaceAcrossProviders()
    {
        var template = CreateWorldTemplate(CreateHeightMap());
        var instance = new WorldInstance(template, 0, true, 1);
        var geoData = new AiGeoDataManager(template);

        var templateFound = template.TryGetHeight(0.5f, 0.5f, out var templateHeight);
        var instanceFound = instance.TryGetHeight(0.5f, 0.5f, out var instanceHeight);
        var geoDataFound = geoData.TryGetHeight(new Vector3(0.5f, 0.5f, 200f), out var geoDataHeight);

        await Assert.That(templateFound).IsTrue();
        await Assert.That(templateHeight).IsEqualTo(0f);
        await Assert.That(instanceFound).IsTrue();
        await Assert.That(instanceHeight).IsEqualTo(0f);
        await Assert.That(geoDataFound).IsTrue();
        await Assert.That(geoDataHeight).IsEqualTo(0f);
    }

    [Test]
    public async Task TryGetHeight_SeaLevelBaiPoint_IsAValidSurface()
    {
        var template = CreateWorldTemplate(CreateHeightMap(50));
        var bai = new BaseBaiLoader(template);
        var netMission = new NetMissionReader(Stream.Null, 1);
        netMission.NodeDescriptorList.TryAdd(1, new NodeDescriptor(netMission)
        {
            Id = 1,
            Pos = new Vector3(0.5f, 0.5f, 0f)
        });
        bai.NetMissionReaders.Add(netMission);
        template.ZoneBaiLoader.Add(1, bai);
        var geoData = new AiGeoDataManager(template);

        var found = geoData.TryGetHeight(new Vector3(0.5f, 0.5f, 10f), out var height);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(0f);
    }

    [Test]
    public async Task TryGetHeight_InvalidCoordinates_ReturnsUnavailableWithoutIndexing()
    {
        var template = CreateWorldTemplate(CreateHeightMap());
        var instance = new WorldInstance(template, 0, true, 1);
        var geoData = new AiGeoDataManager(template);

        await Assert.That(template.TryGetRawHeightMapHeight(-1, 0, out _)).IsFalse();
        await Assert.That(template.TryGetHeight(-0.1f, 0f, out var unavailableHeight)).IsFalse();
        await Assert.That(unavailableHeight).IsEqualTo(0f);
        await Assert.That(template.TryGetHeight(float.NaN, 0f, out _)).IsFalse();
        await Assert.That(template.TryGetHeight(float.PositiveInfinity, 0f, out _)).IsFalse();
        await Assert.That(template.TryGetHeight(0f, float.NegativeInfinity, out _)).IsFalse();
        await Assert.That(template.TryGetHeight(WorldManager.CELL_SIZE, 0f, out _)).IsFalse();
        await Assert.That(template.TryGetHeight(0f, WorldManager.CELL_SIZE, out _)).IsFalse();
        await Assert.That(instance.TryGetHeight(WorldManager.CELL_SIZE, 0f, out _)).IsFalse();
        await Assert.That(geoData.TryGetHeight(new Vector3(-1f, 0f, 0f), out _)).IsFalse();
        await Assert.That(template.GetBaiByPos(new Vector3(-1f, 0f, 0f))).IsNull();
        await Assert.That(template.GetCell(1, 0)).IsNull();
        await Assert.That(template.Cells[0, 0].GetHeightMapDataInCell(WorldManager.CELL_HMAP_RESOLUTION, 0)).IsEqualTo(0f);
    }

    [Test]
    public async Task TryGetHeight_InternalCellSeam_UsesAdjacentCellSamples()
    {
        var template = CreateWorldTemplate(2, 1);
        SetLoadedCell(template, 0, 0, CreateHeightMap(10));
        SetLoadedCell(template, 1, 0, CreateHeightMap(42));

        var found = template.TryGetHeight(WorldManager.CELL_SIZE - 1f, 0f, out var height);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(26f);
    }

    [Test]
    public async Task TryGetHeight_FinalOuterStripWithoutEndpoint_ReturnsUnavailable()
    {
        var template = CreateWorldTemplate(CreateHeightMap(10));

        var found = template.TryGetHeight(WorldManager.CELL_SIZE - 1f, 0f, out _);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task TryGetHeight_FinalStoredLatticePoint_DoesNotRequireUnusedEndpoint()
    {
        var heightMap = CreateHeightMap(10);
        heightMap[WorldManager.CELL_HMAP_RESOLUTION - 1, WorldManager.CELL_HMAP_RESOLUTION - 1] = 73;
        var template = CreateWorldTemplate(heightMap);

        var found = template.TryGetHeight(WorldManager.CELL_SIZE - 2f, WorldManager.CELL_SIZE - 2f, out var height);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(73f);
    }

    [Test]
    public async Task TryGetHeight_LoadedCellWithoutTerrainData_ReturnsUnavailable()
    {
        var template = CreateWorldTemplate(1, 1);
        var cell = new WorldCell(0, 0, template);
        SetPrivateProperty(cell, nameof(WorldCell.Loaded), true);
        template.Cells[0, 0] = cell;

        var found = template.TryGetHeight(0f, 0f, out var height);

        await Assert.That(found).IsFalse();
        await Assert.That(height).IsEqualTo(0f);
    }

    private static ushort[,] CreateSingleRaisedCornerHeightMap()
    {
        var heightMap = CreateHeightMap();
        heightMap[1, 1] = 100;
        return heightMap;
    }

    private static ushort[,] CreateHeightMap(ushort height = 0)
    {
        var heightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        if (height == 0)
            return heightMap;

        for (var x = 0; x < heightMap.GetLength(0); x++)
        for (var y = 0; y < heightMap.GetLength(1); y++)
            heightMap[x, y] = height;

        return heightMap;
    }

    private static WorldTemplate CreateWorldTemplate(ushort[,] heightMap)
    {
        var template = CreateWorldTemplate(1, 1);
        SetLoadedCell(template, 0, 0, heightMap);
        return template;
    }

    private static WorldTemplate CreateWorldTemplate(int cellCountX, int cellCountY)
    {
        return new WorldTemplate
        {
            Name = "test_world",
            CellX = cellCountX,
            CellY = cellCountY,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[cellCountX, cellCountY]
        };
    }

    private static BaseBaiLoader AddNetMissionBai(WorldTemplate template, Vector3 position,
        BaiNavigationType navigationType)
    {
        var bai = new BaseBaiLoader(template);
        var netMission = new NetMissionReader(Stream.Null, 1);
        netMission.NodeDescriptorList.TryAdd(1, new NodeDescriptor(netMission)
        {
            Id = 1,
            Pos = position,
            NavigationType = navigationType
        });
        bai.NetMissionReaders.Add(netMission);
        template.ZoneBaiLoader.Add(1, bai);
        return bai;
    }

    private static void SetLoadedCell(WorldTemplate template, int cellX, int cellY, ushort[,] heightMap)
    {
        var cell = new WorldCell(cellX, cellY, template);
        SetPrivateProperty(cell, nameof(WorldCell.HeightMap), heightMap);
        SetPrivateProperty(cell, nameof(WorldCell.Loaded), true);
        template.Cells[cellX, cellY] = cell;
    }

    private static void SetPrivateProperty<T>(WorldCell cell, string propertyName, T value)
    {
        var property = typeof(WorldCell).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        property!.SetValue(cell, value);
    }
}
