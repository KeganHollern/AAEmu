using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Mission;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World;

[NotInParallel]
public sealed class ShipStaticBarrierTerrainTests
{
    [Test]
    [Arguments(false, 40, false)]
    [Arguments(true, 40, true)]
    [Arguments(false, 100, true)]
    public async Task CoastalClassificationDistinguishesMissingTerrainFromZeroAndAnAuthoredPier(
        bool hasTerrain, int polygonZ, bool expectedBarrier)
    {
        var previous = AppConfiguration.Instance.World;
        var template = WorldTemplateTests.CreateWorldTemplate(1, 1);
        var zeroTerrain = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        WorldTemplateTests.SetLoadedCell(template, 0, 0, hasTerrain ? zeroTerrain : null);
        var reader = new AreasMissionReader(Stream.Null, 1);
        reader.ForbiddenBoundariesList.Add(new AiShape(1)
        {
            Name = "Coastal boundary",
            Points = [new(64, 64, polygonZ), new(72, 64, polygonZ), new(68, 72, polygonZ)]
        });
        var loader = new BaseBaiLoader(template);
        loader.AreasMissionReaders.Add(reader);
        template.GetCell(0, 0).BaiLoader[0, 0] = loader;
        var world = new WorldInstance(template, 0, true, 0)
        {
            ShipStaticBarriers = new(),
            Water = new WaterBodies { OceanLevel = template.OceanLevel }
        };
        GC.SuppressFinalize(world);
        try
        {
            AppConfiguration.Instance.World = new() { GeoDataMode = true };
            world.EnsureShipStaticBarrierBaiCell(0, 0);
            world.EnsureShipStaticBarrierBaiCell(0, 0);
            await Assert.That(world.ShipStaticBarriers.Barriers.Count).IsEqualTo(expectedBarrier ? 1 : 0);
        }
        finally
        {
            AppConfiguration.Instance.World = previous;
        }
    }
}
