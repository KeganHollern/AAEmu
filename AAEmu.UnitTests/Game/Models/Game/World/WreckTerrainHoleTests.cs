using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World;

public sealed class WreckTerrainHoleTests
{
    [Test]
    [Arguments("main_world", 17128.2f, 19756.2f, 100f, 73.973145f, 69f, 58.79947f)]
    [Arguments("arche_mall_world", 3424.2f, 4698.2f, 115f, 109.86601f, 100f, 88.309814f)]
    public async Task StockHole_UsesTheHighestSolidBelowTheRackWithoutNavigation(string map, float x, float y,
        float upperRack, float upperFloor, float lowerRack, float lowerFloor)
    {
        var archive = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(archive), "Set AAEMU_HOUSING_GAME_PAK to check the stock wreck sites.");
        var source = new ClientSource { PathName = archive, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            var cellX = (int)(x / WorldManager.CELL_SIZE);
            var cellY = (int)(y / WorldManager.CELL_SIZE);
            var cells = $"game/worlds/{map}/cells/";
            var cell = $"{cells}{cellX:000}_{cellY:000}/";
            var assets = new HousingGeometryAssets(path =>
            {
                if (path.StartsWith(cells, StringComparison.Ordinal) && !path.StartsWith(cell, StringComparison.Ordinal))
                    return null;
                return source.FileExists(path) ? source.GetFileStream(path) : null;
            }, (_, _) => []);
            var template = WorldTemplateTests.CreateWorldTemplate(cellX + 1, cellY + 1);
            template.Name = map;
            template.GeoData = new AiGeoDataManager(template);
            var ship = new Slave();
            var world = new WorldInstance(template, 0, true, ship.Transform.InstanceId);
            ship.ParentWorld = world;
            world.SlaveManager = new SlaveManager(world) { PlacementAssets = assets };
            world.Water.OceanLevel = 100;
            await Assert.That(assets.GetTerrain(template, cellX, cellY).IsHole(x, y)).IsTrue();
            await Assert.That(template.TryGetHeight(x, y, out _)).IsFalse();

            foreach (var (rackZ, floorZ) in new[] { (upperRack, upperFloor), (lowerRack, lowerFloor) })
            {
                var rack = new Vector3(x, y, rackZ);
                await Assert.That(ship.ResolveWreckDrop(rack, rack, Slave.WreckDropSurface.Floor).Z).IsEqualTo(floorZ).Within(0.001f);
                var cargoZ = 100f - floorZ >= 30f ? 100f : floorZ;
                await Assert.That(ship.ResolveWreckDrop(rack, rack, Slave.WreckDropSurface.Cargo).Z).IsEqualTo(cargoZ).Within(0.001f);
                await Assert.That(ship.ResolveWreckDrop(rack, rack, Slave.WreckDropSurface.Water).Z).IsEqualTo(100f);
            }

            var below = new Vector3(x, y, lowerFloor - 1f);
            await Assert.That(ship.ResolveWreckDrop(below, below, Slave.WreckDropSurface.Floor)).IsEqualTo(below);
            var start = new Vector3(x, y, upperRack);
            await Assert.That(ship.ResolveWreckDrop(start, below, Slave.WreckDropSurface.Floor).Z).IsEqualTo(upperFloor).Within(0.001f);
        }
        finally
        {
            source.Close();
        }
    }
}
