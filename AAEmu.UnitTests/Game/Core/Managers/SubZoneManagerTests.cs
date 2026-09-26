using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Xml;
using AAEmu.Game.Models.Game.World.Zones;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class SubZoneManagerTests
{
    [Test]
    public void Load_CallsGetWorlds()
    {
        var mockWorld = Mock.Of<IWorldManager>();
        mockWorld.GetAllWorldTemplates().Returns([]);
        var manager = new SubZoneManager(mockWorld.Object, Mock.Of<IZoneManager>().Object);
        manager.Load();

        mockWorld.GetAllWorldTemplates().WasCalled(Times.Once);
    }

    [Test]
    [Arguments(0f, 0f)]
    [Arguments(995f, 1000f)]
    [Arguments(1000f, 1000f)]
    [Arguments(20000f, 30000f)]
    public async Task GetSubZoneByPosition_TranslatedPolygon_UsesUnboundedRay(float x, float y)
    {
        var manager = new SubZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<IZoneManager>().Object);
        var world = new WorldTemplate();
        // The polygon remains in its authored zone even when it crosses a terrain boundary.
        world.SubZones[99] = [Square(524, x, y)];

        await Assert.That(manager.GetSubZoneByPosition(world, x + 5, y + 5)).IsEquivalentTo(new uint[] { 524 });
        await Assert.That(manager.GetSubZoneByPosition(world, x + 20, y + 5)).IsEmpty();
        await Assert.That(manager.GetSubZoneByPosition(world, x - 5, y + 5)).IsEmpty();
        await Assert.That(manager.GetSubZoneByPosition(world, float.NaN, y)).IsEmpty();
        await Assert.That(manager.GetSubZoneByPosition(world, x, float.PositiveInfinity)).IsEmpty();
    }

    [Test]
    public async Task GetSubZoneByPosition_ConcaveOverlapAndWorldBoundary_UsesOnlyContainingPolygons()
    {
        var manager = new SubZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<IZoneManager>().Object);
        var world = new WorldTemplate();
        world.SubZones[1] = [new Area
        {
            Id = 10, Points = [new(0, 0, 0), new(10, 0, 0), new(10, 3, 0),
                new(3, 3, 0), new(3, 10, 0), new(0, 10, 0)]
        }, Square(20, 0, 0)];
        world.SubZones[2] = [Square(20, 0, 0)];

        await Assert.That(manager.GetSubZoneByPosition(world, 1, 1)).IsEquivalentTo(new uint[] { 10, 20 });
        await Assert.That(manager.GetSubZoneByPosition(world, 5, 5)).IsEquivalentTo(new uint[] { 20 });
        await Assert.That(manager.GetSubZoneByPosition(new WorldTemplate(), 1, 1)).IsEmpty();
        await Assert.That(manager.GetSubZoneByPosition(world, 0, 5)).IsEmpty();
        await Assert.That(manager.GetSubZoneByPosition(world, 10, 5)).IsEquivalentTo(new uint[] { 20 });
    }

    [Test]
    public async Task ReadSubZones_AppliesCellOriginScaleAndRotation_UsesAreaIdAndDeduplicates()
    {
        const string Xml = """
            <Objects>
              <Entity Name="rotated" Pos="10,20,30" cellX="2" cellY="3"
                      Scale="2,3,1" Rotate="0.70710678,0,0,0.70710678">
                <Area Id="524" Group="18" value1="999"><Points>
                  <Point Pos="0,0,0"/><Point Pos="10,0,0"/>
                  <Point Pos="10,10,0"/><Point Pos="0,10,0"/>
                </Points></Area>
                <Area Id="999" Group="1"><Points/></Area>
              </Entity>
            </Objects>
            """;
        var areas = Area.ReadSubZones(["regional/subzone_area.xml", "subzone_area.xml"], _ => Xml,
            new XmlWorldZone { OriginX = 5, OriginY = 7 });

        await Assert.That(areas.Count).IsEqualTo(1);
        var area = areas[0];
        await Assert.That(area.Id).IsEqualTo(524u);
        var offset = new Vector3(7 * 1024 + 10, 10 * 1024 + 20, 30);
        await Assert.That(Vector3.Distance(area.Points[0], offset) < 0.001f).IsTrue();
        await Assert.That(Vector3.Distance(area.Points[1], offset + new Vector3(0, 20, 0)) < 0.001f).IsTrue();
        await Assert.That(Vector3.Distance(area.Points[2], offset + new Vector3(-30, 20, 0)) < 0.001f).IsTrue();
        await Assert.That(Point.IsInside(area.Points, area.Points.Count, offset + new Vector3(-15, 10, 0))).IsTrue();
    }

    [Test]
    public async Task GetSubZoneByPosition_AuthoredHeight_UsesInclusiveUnscaledVolume()
    {
        const string Xml = """
            <Objects><Entity Name="height" Pos="0,0,100" Scale="2,2,2">
              <Area Id="524" Group="18" Height="50"><Points>
                <Point Pos="0,0,10"/><Point Pos="10,0,20"/><Point Pos="10,10,20"/><Point Pos="0,10,20"/>
              </Points></Area>
              <Area Id="524" Group="18" Height="0"><Points>
                <Point Pos="0,0,10"/><Point Pos="10,0,20"/><Point Pos="10,10,20"/><Point Pos="0,10,20"/>
              </Points></Area>
            </Entity></Objects>
            """;
        var areas = Area.ReadSubZones(["subzone_area.xml"], _ => Xml, new XmlWorldZone());
        await Assert.That(areas.Count).IsEqualTo(2);
        await Assert.That(areas[0].Height).IsEqualTo(50f);
        var world = new WorldTemplate();
        world.SubZones[1] = [areas[0]];
        var manager = new SubZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<IZoneManager>().Object);
        foreach (var z in new float[] { 120, 140, 170 })
            await Assert.That(manager.GetSubZoneByPosition(world, new Vector3(5, 5, z))).IsEquivalentTo(new uint[] { 524 });
        foreach (var z in new float[] { 119, 171, float.NaN, float.PositiveInfinity })
            await Assert.That(manager.GetSubZoneByPosition(world, new Vector3(5, 5, z))).IsEmpty();
        await Assert.That(manager.GetSubZoneByPosition(world, 5, 5)).IsEquivalentTo(new uint[] { 524 });
        world.SubZones[1] = [areas[1]];
        await Assert.That(manager.GetSubZoneByPosition(world, new Vector3(5, 5, -1000))).IsEquivalentTo(new uint[] { 524 });
        await Assert.That(manager.GetSubZoneByPosition(world, new Vector3(5, 5, 1000))).IsEquivalentTo(new uint[] { 524 });
    }

    [Test]
    public async Task GetSubZoneByPosition_AuthoredZeroId_DoesNotHideValidMembership()
    {
        var world = new WorldTemplate();
        world.SubZones[1] = [Square(0, 0, 0), Square(524, 0, 0)];
        var manager = new SubZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<IZoneManager>().Object);
        await Assert.That(manager.GetSubZoneByPosition(world, new Vector3(5, 5, 0))).IsEquivalentTo(new uint[] { 524 });
        await Assert.That(manager.GetSubZoneByPosition(world, 5, 5)).IsEquivalentTo(new uint[] { 524 });
    }

    [Test]
    public async Task ReadSubZones_ExactClient_AllAuthoredShapesLoad()
    {
        var root = Environment.GetEnvironmentVariable("AAEMU_SUBZONE_CLIENT_ROOT");
        Skip.Unless(!string.IsNullOrEmpty(root), "Set AAEMU_SUBZONE_CLIENT_ROOT to the extracted r208022 subzone files.");
        var files = Directory.GetFiles(root!, "subzone_area.xml", SearchOption.AllDirectories);
        var count = 0;
        foreach (var file in files)
            count += Area.ReadSubZones([file], File.ReadAllText, new XmlWorldZone()).Count;

        await Assert.That(files.Length).IsEqualTo(115);
        await Assert.That(count).IsEqualTo(1047);
    }

    private static Area Square(uint id, float x, float y) => new()
    {
        Id = id,
        Points = [new(x, y, 0), new(x + 10, y, 0), new(x + 10, y + 10, 0), new(x, y + 10, 0)]
    };
}
