using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
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

    private static ushort[,] CreateSingleRaisedCornerHeightMap()
    {
        var heightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        heightMap[1, 1] = 100;
        return heightMap;
    }

    private static WorldTemplate CreateWorldTemplate(ushort[,] heightMap)
    {
        var template = new WorldTemplate
        {
            Name = "test_world",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };
        var cell = new WorldCell(0, 0, template);
        SetPrivateProperty(cell, nameof(WorldCell.HeightMap), heightMap);
        SetPrivateProperty(cell, nameof(WorldCell.Loaded), true);
        template.Cells[0, 0] = cell;
        return template;
    }

    private static void SetPrivateProperty<T>(WorldCell cell, string propertyName, T value)
    {
        var property = typeof(WorldCell).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        property!.SetValue(cell, value);
    }
}
