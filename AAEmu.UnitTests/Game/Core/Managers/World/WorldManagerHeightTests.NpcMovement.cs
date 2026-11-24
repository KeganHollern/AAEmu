using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

using CgfConverter.Structs;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    private const float DeckZ = 6f;
    private const float Step = 0.5f;
    private readonly List<Action> _restoreMovementServices = [];

    [Test]
    public async Task MoveTowards_ContinuousDeckAboveTerrain_KeepsFloorAcrossFiveSteps()
    {
        var npc = CreateDeckWalker();
        var world = npc.ParentWorld.Template;

        // Ezna's pier changes from a human waypoint to outdoor triangulation on the third half-metre step.
        world.GeoData.TryGetGroundSurface(new Vector3(111f, 100f, DeckZ), out var before);
        world.GeoData.TryGetGroundSurface(new Vector3(110.5f, 100f, DeckZ), out var after);
        await Assert.That(before.Source).IsEqualTo(GroundSurfaceSource.NavigationNode);
        await Assert.That(after.Source).IsEqualTo(GroundSurfaceSource.Terrain);
        await Assert.That(after.Height).IsEqualTo(0f);

        for (var i = 1; i <= 5; i++)
        {
            npc.MoveTowards(new Vector3(105f, 100f, DeckZ), Step, rangeTolerance: 0f);
            await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(112f - i * Step, 100f, DeckZ));
            await Assert.That(npc.Movements[^1].Z).IsEqualTo(DeckZ);
        }
        await Assert.That(npc.Movements.Count).IsEqualTo(5);
    }

    [Test]
    public async Task MoveTowards_LeavesDeckAtRealEdge_DescendsToTerrain()
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(110.5f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(110.5f, 99f, DeckZ), 1f, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 99f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_TerrainWithoutDeck_UsesTerrainHeight()
    {
        var npc = CreateDeckWalker(hasDeck: false);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(105f, 100f, DeckZ), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 100f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_GapWithinOneBrush_ReleasesPreviousFloor()
    {
        var npc = CreateDeckWalker(hasGap: true);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(105f, 100f, DeckZ), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 100f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_TerrainBelowDeck_StaysOnTerrain()
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(110.5f, 100f, 0f);
        npc.MoveTowards(new Vector3(105f, 100f, 0f), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110f, 100f, 0f));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_AuthoredDescentThroughDeck_UsesAuthoredHeight()
    {
        var npc = CreateDeckWalker();
        var destination = new Vector3(110.5f, 100f, 3f);
        npc.MoveTowards(destination, 4f, rangeTolerance: 0f, authoredPathZ: true);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(destination);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(3f);
    }

    [Test]
    [Arguments(0.0001f)]
    [Arguments(-0.0001f)]
    [Arguments(0.002f)]
    [Arguments(-0.002f)]
    public async Task MoveTowards_SmallTargetHeightDifference_KeepsDeckHeight(float heightDifference)
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(110.5f, 100f, DeckZ + heightDifference), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position.X).IsEqualTo(110.5f).Within(0.0001f);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(DeckZ);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(DeckZ);
    }

    [Test]
    [Arguments(0.5f)]
    [Arguments(2f)]
    public async Task MoveTowards_ClearlyLowerTarget_UsesTerrainHeight(float targetDrop)
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(110.5f, 100f, DeckZ);
        // For the 0.5 m descent, interpolation alone changes the first step by less than 1 cm.
        npc.MoveTowards(new Vector3(78.5f, 100f, DeckZ - targetDrop), Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position.X).IsLessThan(110.5f);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(0f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task MoveTowards_MissingModelOverlapsDeck_UsesNextSupportingBrush()
    {
        var modelReads = new List<string>();
        var npc = CreateDeckWalker(overlappingModelReads: modelReads);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);
        npc.MoveTowards(new Vector3(110.5f, 100f, DeckZ), Step, rangeTolerance: 0f);

        await Assert.That(modelReads.SequenceEqual(["objects/missing.cgf", "objects/deck.cgf"])).IsTrue();
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(new Vector3(110.5f, 100f, DeckZ));
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(DeckZ);
    }

    private MovementProbeNpc CreateDeckWalker(bool hasDeck = true, bool hasGap = false, List<string> overlappingModelReads = null)
    {
        AppConfiguration.Instance.World.GeoDataMode = true;
        var manager = CreateManagerWithFlatHeightTemplate(worldName: "main_world");
        var template = manager.GetWorldTemplateByZoneKey(100);
        var skills = new SkillManager(null, null);
        SetPrivateMember(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        UseMovementService(manager);
        UseMovementService(skills);

        var net = new NetMissionReader(Stream.Null, 1);
        net.NodeDescriptorList[3597] = new NodeDescriptor(net)
        {
            Id = 3597, NavigationType = BaiNavigationType.WaypointHuman, Pos = new(116.05f, 100.257f, DeckZ)
        };
        net.NodeDescriptorList[2704] = new NodeDescriptor(net)
        {
            Id = 2704, NavigationType = BaiNavigationType.Triangular, Pos = new(106.595f, 103.688f, DeckZ - 1.09752f)
        };
        var bai = new BaseBaiLoader(template);
        bai.NetMissionReaders.Add(net);
        var vertices = new VertexMissionReader(Stream.Null, 1);
        vertices.ObstacleDataDescriptorList.Add(new ObstacleDataDescriptor(1)
        {
            Pos = new(106.883f, 99.101f, DeckZ - 4.24935f)
        });
        bai.VertexMissionReaders.Add(vertices);
        template.ZoneBaiLoader.Add(1, bai);

        var bounds = new CryBounds(new(107f, 99.5f, DeckZ), new(114.5f, 100.5f, DeckZ));
        // The continuous deck shares its diagonal. The gap splits the same brush into disconnected faces.
        var mesh = hasGap
            ? new CryTriangleMesh(
                [bounds.Min, new(110.65f, 99.5f, DeckZ), new(110.65f, 100.5f, DeckZ), new(107f, 100.5f, DeckZ),
                 new(110.85f, 99.5f, DeckZ), new(114.5f, 99.5f, DeckZ), bounds.Max, new(110.85f, 100.5f, DeckZ)],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7], [])
            : new CryTriangleMesh(
                [bounds.Min, new(114.5f, 99.5f, DeckZ), bounds.Max, new(107f, 100.5f, DeckZ)],
                [0, 1, 2, 0, 2, 3], []);
        var part = new CryGeometryPart(mesh, Matrix4x4.Identity, 4096, "", "deck");
        var geometry = new CryGeometryAsset(bounds, [part]);
        template.GeoData = new AiGeoDataManager(template)
        {
            GroundBrushGeometry = brush =>
            {
                var path = brush.ModelUri;
                overlappingModelReads?.Add(path);
                return path == "objects/deck.cgf" ? geometry : throw new FileNotFoundException("Missing fixture model.", path);
            }
        };
        if (hasDeck)
        {
            var objects = new ObjectsFile("main_world/cells/000_000/client/object.dat")
            {
                AssetPathsList = [new AssetPath { Name = "objects/deck.cgf" }],
                PrefabsList = [new ObjectDataType1Brush
                {
                    StartPos = bounds.Min, EndPos = bounds.Max, Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1 }
                }]
            };
            if (overlappingModelReads != null)
            {
                objects.AssetPathsList.Add(new AssetPath { Name = "objects/missing.cgf" });
                objects.PrefabsList.Insert(0, new ObjectDataType1Brush
                {
                    PathId = 1, StartPos = bounds.Min, EndPos = bounds.Max,
                    Matrix3X4 = new Matrix3x4 { M11 = 1, M22 = 1, M33 = 1 }
                });
            }
            template.Cells[0, 0].LoadedObjectDat = objects;
        }
        var npc = new MovementProbeNpc { Hp = 100, DisabledSetPosition = true };
        npc.ParentWorld = new WorldInstance(template, 1, true, npc.Transform.InstanceId);
        SetPrivateMember(npc.Transform, "_zoneId", 100u);
        npc.Transform.Local.SetPosition(112f, 100f, DeckZ);
        npc.Ai = new DummyAiCharacter { Owner = npc, HomePosition = npc.Transform.Local.Position };
        return npc;
    }

    private void UseMovementService<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        field.SetValue(null, manager);
        _restoreMovementServices.Add(() => field.SetValue(null, previous));
    }

    [After(Test)]
    public void RestoreMovementServices()
    {
        foreach (var restore in _restoreMovementServices)
            restore();
        _restoreMovementServices.Clear();
    }
}
