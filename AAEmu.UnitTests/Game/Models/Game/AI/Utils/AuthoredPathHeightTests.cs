using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.Utils;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.AI.Utils;

/// <summary>
/// aaemu-cluster#92: authored .path waypoint heights own the scripted walk, terrain still corrects
/// outdoor recorded routes.
/// </summary>
public class AuthoredPathHeightTests
{
    [Test]
    public async Task ResolveAuthoredPathHeight_NavigationSourcedSurface_PreservesAuthoredHeight()
    {
        // Indoors: the nearest BAI navigation node sits above the real brush floor the route was recorded on
        var template = CreateWorldTemplate(50);
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.WaypointHuman);
        var npc = AttachToWorld(new Npc(), template);
        const float authoredZ = 89.2f;

        var resolved = AiUtils.ResolveAuthoredPathHeight(npc, 0.5f, 0.5f, authoredZ);

        await Assert.That(template.GeoData.TryGetGroundSurface(new Vector3(0.5f, 0.5f, authoredZ), out var surface)).IsTrue();
        await Assert.That(surface.Source).IsEqualTo(GroundSurfaceSource.NavigationNode);
        await Assert.That(resolved).IsEqualTo(authoredZ);
    }

    [Test]
    public async Task ResolveAuthoredPathHeight_TerrainSourcedSurface_AppliesTerrainCorrection()
    {
        // Outdoors: triangular BAI nodes only describe topology, rendered terrain owns the height
        var template = CreateWorldTemplate(50);
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.Triangular);
        var npc = AttachToWorld(new Npc(), template);

        var resolved = AiUtils.ResolveAuthoredPathHeight(npc, 0.5f, 0.5f, 50.4f);

        await Assert.That(template.GeoData.TryGetGroundSurface(new Vector3(0.5f, 0.5f, 50.4f), out var surface)).IsTrue();
        await Assert.That(surface.Source).IsEqualTo(GroundSurfaceSource.Terrain);
        await Assert.That(resolved).IsEqualTo(50f);
    }

    [Test]
    public async Task ResolveAuthoredPathHeight_TerrainDisagreesBeyondCorrectionLimit_PreservesAuthoredHeight()
    {
        // Routes recorded on top of geometry (bridges, decks) must not be dropped onto the terrain below
        var template = CreateWorldTemplate(50);
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.Triangular);
        var npc = AttachToWorld(new Npc(), template);

        var resolved = AiUtils.ResolveAuthoredPathHeight(npc, 0.5f, 0.5f, 56f);

        await Assert.That(resolved).IsEqualTo(56f);
    }

    [Test]
    public async Task ResolveAuthoredPathHeight_FlyingNpc_PreservesAuthoredHeight()
    {
        var template = CreateWorldTemplate(50);
        var npc = AttachToWorld(new Npc { CanFly = true }, template);

        var resolved = AiUtils.ResolveAuthoredPathHeight(npc, 0.5f, 0.5f, 120f);

        await Assert.That(resolved).IsEqualTo(120f);
    }

    [Test]
    public async Task ResolveAuthoredPathHeight_NpcWithoutWorld_PreservesAuthoredHeight()
    {
        var resolved = AiUtils.ResolveAuthoredPathHeight(new Npc(), 0.5f, 0.5f, 248.2f);

        await Assert.That(resolved).IsEqualTo(248.2f);
    }

    [Test]
    public async Task HasReachedPathWaypoint_ResidualVerticalError_StillArrives()
    {
        // The indoor nav-vs-floor gap measured in Sharpwind Mines is ~0.7m; a 3D test never converged
        var waypoint = new Vector3(741f, 326.6f, 248.2f);
        var position = new Vector3(741.1f, 326.6f, 248.9f);

        await Assert.That(AiUtils.HasReachedPathWaypoint(position, waypoint, 0.5f)).IsTrue();
    }

    [Test]
    public async Task HasReachedPathWaypoint_HorizontallyShort_DoesNotArrive()
    {
        var waypoint = new Vector3(741f, 326.6f, 248.2f);
        var position = new Vector3(743f, 326.6f, 248.2f);

        await Assert.That(AiUtils.HasReachedPathWaypoint(position, waypoint, 0.5f)).IsFalse();
    }

    [Test]
    public async Task HasReachedPathWaypoint_DifferentFloor_DoesNotArrive()
    {
        var waypoint = new Vector3(741f, 326.6f, 248.2f);
        var position = new Vector3(741f, 326.6f, 254f);

        await Assert.That(AiUtils.HasReachedPathWaypoint(position, waypoint, 0.5f)).IsFalse();
    }

    private static WorldTemplate CreateWorldTemplate(ushort? groundHeight)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "authored_path_height_test",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };

        var cell = new WorldCell(0, 0, template);
        if (groundHeight.HasValue)
            SetPrivateMember(cell, nameof(WorldCell.HeightMap), CreateHeightMap(groundHeight.Value));
        SetPrivateMember(cell, nameof(WorldCell.Loaded), true);
        template.Cells[0, 0] = cell;
        template.GeoData = new AiGeoDataManager(template);
        return template;
    }

    private static ushort[,] CreateHeightMap(ushort height)
    {
        var heightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        for (var x = 0; x < heightMap.GetLength(0); x++)
        for (var y = 0; y < heightMap.GetLength(1); y++)
            heightMap[x, y] = height;

        return heightMap;
    }

    private static void AddNetMissionBai(WorldTemplate template, Vector3 position,
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
    }

    private static Npc AttachToWorld(Npc npc, WorldTemplate template)
    {
        var world = new WorldInstance(template, 1, true, 1);
        SetPrivateMember(npc, "_parentWorld", world, typeof(GameObject));
        return npc;
    }

    private static void SetPrivateMember(object target, string name, object value, Type declaringType = null)
    {
        var type = declaringType ?? target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }
}
