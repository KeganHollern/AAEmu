using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.AI.AStar;
using AAEmu.Game.Models.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.AI.AStar;

[NotInParallel]
public sealed class PathNodeTests
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
            World = new WorldConfig { GeoDataMode = true }
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
    public async Task FindPath_CompetingRoutes_ReturnsLowestCumulativeCost()
    {
        var graph = CreateGraph(
            [
                new Vector3(10f, 10f, 0f),
                new Vector3(10f, 20f, 0f),
                new Vector3(19f, 10f, 0f),
                new Vector3(19f, 30f, 0f),
                new Vector3(20f, 10f, 0f)
            ],
            [
                new TestEdge(0, 1),
                new TestEdge(1, 4),
                new TestEdge(0, 2),
                new TestEdge(2, 3),
                new TestEdge(3, 4)
            ]);
        var pathNode = new PathNode { ZoneKey = TestZoneKey };

        var result = pathNode.FindPath(graph.World, graph.Nodes[0].Pos, graph.Nodes[4].Pos);

        await Assert.That(pathNode.LastSearchSucceeded).IsTrue();
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0]).IsEqualTo(graph.Nodes[0].Pos);
        await Assert.That(result[1]).IsEqualTo(graph.Nodes[1].Pos);
        await Assert.That(result[2]).IsEqualTo(graph.Nodes[4].Pos);
    }

    [Test]
    public async Task FindPath_AgentTooWideForShortRoute_UsesPassableRoute()
    {
        var graph = CreateGraph(
            [
                new Vector3(10f, 10f, 0f),
                new Vector3(15f, 10f, 0f),
                new Vector3(15f, 15f, 0f),
                new Vector3(20f, 10f, 0f)
            ],
            [
                new TestEdge(0, 1, 0.5d),
                new TestEdge(1, 3, 0.5d),
                new TestEdge(0, 2, 2d),
                new TestEdge(2, 3, 2d)
            ]);
        var pathNode = new PathNode { ZoneKey = TestZoneKey };

        var result = pathNode.FindPath(graph.World, graph.Nodes[0].Pos, graph.Nodes[3].Pos, 1f);

        await Assert.That(pathNode.LastSearchSucceeded).IsTrue();
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[1]).IsEqualTo(graph.Nodes[2].Pos);
    }

    [Test]
    public async Task FindPath_DisconnectedGoal_ReturnsFailureWithoutDirectSegment()
    {
        var graph = CreateGraph(
            [
                new Vector3(10f, 10f, 0f),
                new Vector3(15f, 10f, 0f),
                new Vector3(20f, 10f, 0f)
            ],
            [new TestEdge(0, 1)]);
        var pathNode = new PathNode { ZoneKey = TestZoneKey };

        var result = pathNode.FindPath(graph.World, graph.Nodes[0].Pos, graph.Nodes[2].Pos);

        await Assert.That(result).IsEmpty();
        await Assert.That(pathNode.LastSearchSucceeded).IsFalse();
    }

    [Test]
    public async Task NeedsPathRefresh_TargetMovesBeyondThreshold_RefreshesOnlyWhenNeeded()
    {
        var graph = CreateGraph(
            [
                new Vector3(10f, 10f, 0f),
                new Vector3(15f, 10f, 0f),
                new Vector3(20f, 10f, 0f)
            ],
            [new TestEdge(0, 1), new TestEdge(1, 2)]);
        var pathNode = new PathNode { ZoneKey = TestZoneKey };
        var result = pathNode.FindPath(graph.World, graph.Nodes[0].Pos, graph.Nodes[2].Pos);
        pathNode.FoundPath = new Queue<Vector3>(result);

        await Assert.That(pathNode.NeedsPathRefresh(graph.Nodes[2].Pos, 1f, true)).IsFalse();
        await Assert.That(pathNode.NeedsPathRefresh(graph.Nodes[2].Pos + new Vector3(0.5f, 0f, 0f), 1f, true)).IsFalse();
        await Assert.That(pathNode.NeedsPathRefresh(graph.Nodes[2].Pos + new Vector3(2f, 0f, 0f), 1f, true)).IsTrue();

        pathNode.FoundPath.Clear();
        await Assert.That(pathNode.NeedsPathRefresh(graph.Nodes[2].Pos, 1f, true)).IsTrue();
        await Assert.That(pathNode.NeedsPathRefresh(graph.Nodes[2].Pos, 1f, false)).IsFalse();
    }

    [Test]
    public async Task ReducePath_DetourOutsideCorridor_PreservesTopology()
    {
        var graph = CreateGraph([], []);
        var source = new List<Vector3>
        {
            new(10f, 10f, 0f),
            new(10f, 15f, 0f),
            new(20f, 15f, 0f),
            new(20f, 10f, 0f)
        };

        var result = graph.Template.GeoData.ReducePath(source, 10, TestZoneKey).ToArray();

        await Assert.That(result.Count).IsEqualTo(source.Count);
        for (var index = 0; index < source.Count; index++)
            await Assert.That(result[index]).IsEqualTo(source[index]);
    }

    [Test]
    public async Task ReducePath_CollinearNodes_CollapsesToEndpoints()
    {
        var graph = CreateGraph([], []);
        var source = new List<Vector3>
        {
            new(10f, 10f, 0f),
            new(12f, 10f, 0f),
            new(14f, 10f, 0f),
            new(16f, 10f, 0f)
        };

        var result = graph.Template.GeoData.ReducePath(source, 10, TestZoneKey).ToArray();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsEqualTo(source[0]);
        await Assert.That(result[1]).IsEqualTo(source[^1]);
    }

    [Test]
    public async Task GetBaiByPos_MultipleZoneLoaders_SelectsPositionZone()
    {
        var template = CreateWorldTemplate();
        var firstZone = new BaseBaiLoader(template);
        var secondZone = new BaseBaiLoader(template);
        template.ZoneBaiLoader.Add(TestZoneKey, firstZone);
        template.ZoneBaiLoader.Add(SecondTestZoneKey, secondZone);
        template.ZoneKeyByRegions[0, 0] = SecondTestZoneKey;

        await Assert.That(template.GetBaiByPos(new Vector3(10f, 10f, 0f))).IsSameReferenceAs(secondZone);
        await Assert.That(template.GetBaiByPos(TestZoneKey, new Vector3(10f, 10f, 0f))).IsSameReferenceAs(firstZone);
    }

    private const uint TestZoneKey = 1;
    private const uint SecondTestZoneKey = 2;

    private static TestGraph CreateGraph(IReadOnlyList<Vector3> positions, IReadOnlyList<TestEdge> edges)
    {
        var template = CreateWorldTemplate();
        var bai = new BaseBaiLoader(template);
        var netMission = new NetMissionReader(Stream.Null, TestZoneKey);
        var nodes = new Dictionary<int, NodeDescriptor>();
        for (var index = 0; index < positions.Count; index++)
        {
            var node = new NodeDescriptor(netMission)
            {
                Id = index,
                Index = index,
                Pos = positions[index]
            };
            nodes.Add(index, node);
            netMission.NodeDescriptorList.TryAdd(index, node);
        }

        foreach (var edge in edges)
        {
            var sourceNode = nodes[edge.Source];
            var targetNode = nodes[edge.Target];
            netMission.LinkDescriptorList.Add(new LinkDescriptor(netMission)
            {
                SourceNode = sourceNode.Id,
                TargetNode = targetNode.Id,
                SourceNodeDescriptor = sourceNode,
                TargetNodeDescriptor = targetNode,
                MaxPassRadius = edge.MaxPassRadius
            });
        }

        bai.NetMissionReaders.Add(netMission);
        template.ZoneBaiLoader.Add(TestZoneKey, bai);
        template.GeoData = new AiGeoDataManager(template);
        return new TestGraph(template, new WorldInstance(template, 0, true, 1), nodes);
    }

    private static WorldTemplate CreateWorldTemplate()
    {
        var template = new WorldTemplate
        {
            Name = "pathfinding_test",
            CellX = 1,
            CellY = 1,
            Cells = new WorldCell[1, 1],
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        };
        template.Cells[0, 0] = new WorldCell(0, 0, template);
        template.ZoneKeyByRegions[0, 0] = TestZoneKey;
        return template;
    }

    private sealed record TestGraph(
        WorldTemplate Template,
        WorldInstance World,
        IReadOnlyDictionary<int, NodeDescriptor> Nodes);

    private readonly record struct TestEdge(int Source, int Target, double MaxPassRadius = 10d);
}
