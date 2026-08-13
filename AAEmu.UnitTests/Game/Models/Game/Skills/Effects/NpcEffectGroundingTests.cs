using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects;

public class NpcEffectGroundingTests
{
    private const float GroundingTolerance = 5f;

    [Test]
    public async Task ResolveHeight_TriangularNavigationSample_UsesTerrainInsteadOfLegacyHeight()
    {
        var template = CreateWorldTemplate(50);
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.Triangular);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, 53f);

        var legacyFound = template.GeoData.TryGetHeight(endpoint, out var legacyHeight);
        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(legacyFound).IsTrue();
        await Assert.That(legacyHeight).IsEqualTo(90f);
        await Assert.That(result).IsEqualTo(50f);
    }

    [Test]
    public async Task ResolveHeight_WaypointNavigationSample_PreservesNavigationHeight()
    {
        var template = CreateWorldTemplate(50);
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.WaypointHuman);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, 91f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(template.GetHeight(endpoint.X, endpoint.Y)).IsEqualTo(50f);
        await Assert.That(result).IsEqualTo(90f);
    }

    [Test]
    public async Task ResolveHeight_SeaLevelTerrain_AcceptsValidZero()
    {
        var template = CreateWorldTemplate(0);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, 4.5f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(result).IsEqualTo(0f);
    }

    [Test]
    public async Task ResolveHeight_NegativeWaypointHeight_AcceptsResolvedHeight()
    {
        var template = CreateWorldTemplate(50);
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, -2f), BaiNavigationType.WaypointHuman);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, 0f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(result).IsEqualTo(-2f);
    }

    [Test]
    public async Task ResolveHeight_FlyingNpc_PreservesCandidateHeight()
    {
        var template = CreateWorldTemplate(10);
        var npc = AttachToWorld(new Npc { CanFly = true }, template);
        var endpoint = new Vector3(0.5f, 0.5f, 12f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(result).IsEqualTo(endpoint.Z);
    }

    [Test]
    public async Task ResolveHeight_UnavailableGround_PreservesCandidateHeight()
    {
        var template = CreateWorldTemplate(null);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, 12f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(result).IsEqualTo(endpoint.Z);
    }

    [Test]
    public async Task ResolveHeight_NpcWithoutWorld_PreservesCandidateHeight()
    {
        var npc = new Npc();
        var endpoint = new Vector3(0.5f, 0.5f, 12f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(result).IsEqualTo(endpoint.Z);
    }

    [Test]
    public async Task ResolveHeight_DifferenceEqualsTolerance_PreservesCandidateHeight()
    {
        var template = CreateWorldTemplate(10);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, 15f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(result).IsEqualTo(endpoint.Z);
    }

    [Test]
    public async Task ResolveHeight_DifferenceExceedsTolerance_PreservesCandidateHeight()
    {
        var template = CreateWorldTemplate(10);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, 15.1f);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(result).IsEqualTo(endpoint.Z);
    }

    [Test]
    public async Task ResolveHeight_NaNCandidate_PreservesNaN()
    {
        var template = CreateWorldTemplate(10);
        var npc = AttachToWorld(new Npc(), template);
        var endpoint = new Vector3(0.5f, 0.5f, float.NaN);

        var result = NpcEffectGrounding.ResolveHeight(npc, endpoint, GroundingTolerance);

        await Assert.That(float.IsNaN(result)).IsTrue();
    }

    private static WorldTemplate CreateWorldTemplate(ushort? groundHeight)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "npc_effect_grounding_test",
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
