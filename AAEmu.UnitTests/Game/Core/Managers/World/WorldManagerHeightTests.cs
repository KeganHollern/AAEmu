using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

[NotInParallel]
public sealed class WorldManagerHeightTests
{
    private IServiceProvider _previousServiceProvider;
    private ServiceProvider _testServiceProvider;

    [Before(Test)]
    public void SetUp()
    {
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<AppConfiguration>>(Options.Create(new AppConfiguration
        {
            HeightMapsEnable = true,
            World = new WorldConfig { GeoDataMode = false }
        }));
        _testServiceProvider = services.BuildServiceProvider();
        SingletonContainer.ServiceProvider = _testServiceProvider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        _testServiceProvider?.Dispose();
    }

    [Test]
    public async Task GetReferenceHeight_ValidSeaLevelSurface_ReturnsZero()
    {
        var manager = CreateManagerWithFlatHeightTemplate();
        var ai = CreateNpcAi(30.5f);

        var height = manager.GetReferenceHeight(ai, 0.5f, 0.5f, 100f, 100);
        var found = manager.TryGetHeight(100, 0.5f, 0.5f, 100f, out var sampledHeight);

        await Assert.That(found).IsTrue();
        await Assert.That(sampledHeight).IsEqualTo(0f);
        await Assert.That(height).IsEqualTo(0f);
    }

    [Test]
    public async Task GetReferenceHeight_TriangularNavigationNode_UsesTerrainSurface()
    {
        AppConfiguration.Instance.World.GeoDataMode = true;
        var manager = CreateManagerWithFlatHeightTemplate();
        var template = manager.GetWorldTemplateByZoneKey(100);
        var bai = new BaseBaiLoader(template);
        var netMission = new NetMissionReader(Stream.Null, 1);
        netMission.NodeDescriptorList.TryAdd(1, new NodeDescriptor(netMission)
        {
            Id = 1,
            Pos = new System.Numerics.Vector3(12f, 5f, 90f),
            NavigationType = BaiNavigationType.Triangular
        });
        bai.NetMissionReaders.Add(netMission);
        template.ZoneBaiLoader.Add(1, bai);
        template.GeoData = new AiGeoDataManager(template);
        var ai = CreateNpcAi(30.5f);

        var height = manager.GetReferenceHeight(ai, 0.5f, 0.5f, 90f, 100);

        await Assert.That(height).IsEqualTo(0f);
    }

    [Test]
    public async Task GetReferenceHeight_UnavailableSurface_ReturnsRuntimeHomeHeight()
    {
        var manager = CreateManagerWithFlatHeightTemplate();
        var ai = CreateNpcAi(30.5f);

        var height = manager.GetReferenceHeight(ai, WorldManager.CELL_SIZE, 0.5f, 100f, 100);
        var found = manager.TryGetHeight(100, WorldManager.CELL_SIZE, 0.5f, 100f, out var sampledHeight);

        await Assert.That(found).IsFalse();
        await Assert.That(sampledHeight).IsEqualTo(0f);
        await Assert.That(height).IsEqualTo(30.5f);
    }

    [Test]
    public async Task GetReferenceHeight_MissingTerrainData_ReturnsRuntimeHomeHeight()
    {
        var manager = CreateManagerWithFlatHeightTemplate(false);
        var ai = CreateNpcAi(30.5f);

        var height = manager.GetReferenceHeight(ai, 0.5f, 0.5f, 100f, 100);
        var found = manager.TryGetHeight(100, 0.5f, 0.5f, 100f, out var sampledHeight);

        await Assert.That(found).IsFalse();
        await Assert.That(sampledHeight).IsEqualTo(0f);
        await Assert.That(height).IsEqualTo(30.5f);
        var missingTemplate = manager.GetWorldTemplateByZoneKey(100);
        await Assert.That(missingTemplate.Cells[0, 0].Loaded).IsTrue();
        await Assert.That(missingTemplate.Cells[0, 0].HeightMap).IsNull();
    }

    // aaemu-cluster#92 regression, shaped by the live Sharpwind Mines dump: the player's /pos on the
    // floor reads 165.8 while the nearest indoor BAI navigation node answers ~166.5 (+0.7m). Vera's roam
    // targets stay within leash range of home, so a roaming step must keep the retail-capture home floor
    // instead of the lifted nav shelf.
    [Test]
    public async Task GetReferenceHeight_IndoorNavigationNodeNearHome_ReturnsHomeFloor()
    {
        AppConfiguration.Instance.World.GeoDataMode = true;
        var manager = CreateManagerWithFlatHeightTemplate();
        AttachNavigationNode(manager, 166.5f, BaiNavigationType.WaypointHuman);
        var ai = CreateNpcAi(165.8f);

        var height = manager.GetReferenceHeight(ai, 0.5f, 0.5f, 165.8f, 100);

        await Assert.That(height).IsEqualTo(165.8f);
    }

    [Test]
    public async Task GetReferenceHeight_IndoorNavigationNodeChase_ReturnsTargetFloor()
    {
        AppConfiguration.Instance.World.GeoDataMode = true;
        var manager = CreateManagerWithFlatHeightTemplate();
        AttachNavigationNode(manager, 166.5f, BaiNavigationType.WaypointHuman);
        // Home is another wing of the dungeon; only the chased player's client-authoritative floor
        // is a valid anchor here.
        var ai = CreateNpcAi(100f);
        var target = new Npc();
        target.Transform.Local.SetPosition(1f, 1f, 165.8f);
        ai.Owner.CurrentTarget = target;

        var height = manager.GetReferenceHeight(ai, 0.5f, 0.5f, 166.1f, 100);

        await Assert.That(height).IsEqualTo(165.8f);
    }

    [Test]
    public async Task GetReferenceHeight_IndoorNavigationNodeFlyingTarget_FallsBackToHomeFloor()
    {
        AppConfiguration.Instance.World.GeoDataMode = true;
        var manager = CreateManagerWithFlatHeightTemplate();
        AttachNavigationNode(manager, 166.5f, BaiNavigationType.WaypointHuman);
        var ai = CreateNpcAi(165.8f);
        var target = new Npc();
        target.Transform.Local.SetPosition(1f, 1f, 172f);
        ai.Owner.CurrentTarget = target;

        var height = manager.GetReferenceHeight(ai, 0.5f, 0.5f, 165.8f, 100);

        await Assert.That(height).IsEqualTo(165.8f);
    }

    [Test]
    public async Task GetReferenceHeight_IndoorNavigationNodeDifferentLevel_KeepsNavigationHeight()
    {
        AppConfiguration.Instance.World.GeoDataMode = true;
        var manager = CreateManagerWithFlatHeightTemplate();
        AttachNavigationNode(manager, 166.5f, BaiNavigationType.WaypointHuman);
        // Home sits a full level below (stairs/ledge); the nav node is the only surface the server has.
        var ai = CreateNpcAi(158f);

        var height = manager.GetReferenceHeight(ai, 0.5f, 0.5f, 166.5f, 100);

        await Assert.That(height).IsEqualTo(166.5f);
    }

    private static void AttachNavigationNode(WorldManager manager, float nodeZ, BaiNavigationType navigationType)
    {
        var template = manager.GetWorldTemplateByZoneKey(100);
        var bai = new BaseBaiLoader(template);
        var netMission = new NetMissionReader(Stream.Null, 1);
        netMission.NodeDescriptorList.TryAdd(1, new NodeDescriptor(netMission)
        {
            Id = 1,
            Pos = new System.Numerics.Vector3(12f, 5f, nodeZ),
            NavigationType = navigationType
        });
        bai.NetMissionReaders.Add(netMission);
        template.ZoneBaiLoader.Add(1, bai);
        template.GeoData = new AiGeoDataManager(template);
    }

    private static WorldManager CreateManagerWithFlatHeightTemplate(bool hasTerrainData = true)
    {
        var manager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var template = CreateFlatHeightTemplate(hasTerrainData);
        manager.WorldTemplates = new Dictionary<string, WorldTemplate> { { template.Name, template } };
        SetPrivateMember(manager, "_worldIdByZoneKey", new Dictionary<uint, uint> { { 100, template.Id } });
        SetPrivateMember(manager, "WorldNames", new List<string> { string.Empty, template.Name });
        return manager;
    }

    private static WorldTemplate CreateFlatHeightTemplate(bool hasTerrainData)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = hasTerrainData ? "test_world" : $"missing_heightmap_{Guid.NewGuid():N}",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1],
            ZoneKeys = [100]
        };
        var cell = new WorldCell(0, 0, template);
        if (hasTerrainData)
        {
            SetPrivateMember(cell, nameof(WorldCell.HeightMap), new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION]);
            SetPrivateMember(cell, nameof(WorldCell.Loaded), true);
        }
        template.Cells[0, 0] = cell;
        return template;
    }

    private static DummyAiCharacter CreateNpcAi(float homeZ)
    {
        var npc = new Npc();
        var ai = new DummyAiCharacter
        {
            Owner = npc,
            HomePosition = new System.Numerics.Vector3(0f, 0f, homeZ)
        };
        npc.Ai = ai;
        return ai;
    }

    private static void SetPrivateMember(object target, string name, object value)
    {
        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }
}
