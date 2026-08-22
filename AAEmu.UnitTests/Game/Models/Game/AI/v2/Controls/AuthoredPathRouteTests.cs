using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.AI.v2.Controls;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.AI.v2.Params;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Controls;

/// <summary>
/// aaemu-cluster#92: a scripted walk over an authored route has to terminate and hand control back to the
/// command set, even when the geodata resolver disagrees with the recorded waypoint height (indoors the
/// nearest BAI navigation node sits above the brush floor the route was recorded on).
/// </summary>
[NotInParallel]
public sealed class AuthoredPathRouteTests
{
    private const string AlistairRoute = "aipath_alistair0_0";
    private const string OtherRoute = "aipath_ben_day";
    /// <summary>How far above the recorded floor the indoor navigation node sits</summary>
    private const float NavigationOffset = 0.7f;

    private static List<AiPathPoint> Route => AiPathsManager.Instance.LoadAiPathPoints(AlistairRoute);
    private static float AuthoredZ => Route[0].Position.Z;
    private static float NavigationZ => AuthoredZ + NavigationOffset;

    private IServiceProvider _previousServiceProvider;
    private ServiceProvider _testServiceProvider;

    [Before(Test)]
    public void SetUp()
    {
        // Npc.MoveTowards asks SkillManager for the root/snare buff tags
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        var skillManager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetPrivateMember(skillManager, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        var services = new ServiceCollection();
        services.AddSingleton(skillManager);
        _testServiceProvider = services.BuildServiceProvider();
        SingletonContainer.ServiceProvider = _testServiceProvider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        typeof(Singleton<SkillManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, null);
        _testServiceProvider?.Dispose();
    }

    [Test]
    public async Task RunCurrentPath_AuthoredZBelowResolvedSurface_WalksRouteAndReturnsToCommandSet()
    {
        var (npc, ai) = CreateWalker();
        var route = AiPathsManager.Instance.LoadAiPathPoints(AlistairRoute);
        await Assert.That(route.Count).IsGreaterThan(0);

        // The resolver would lift every step ~0.7m above the recorded floor
        await Assert.That(npc.ParentWorld.Template.GeoData.TryGetGroundSurface(
            new Vector3(route[0].Position.X, route[0].Position.Y, AuthoredZ), out var surface)).IsTrue();
        await Assert.That(surface.Source).IsEqualTo(GroundSurfaceSource.NavigationNode);
        await Assert.That(surface.Height).IsEqualTo(NavigationZ);

        ai.LoadAiPathPoints(AlistairRoute, true);
        ai.PathHandler.AiPathPointsRemaining.Enqueue(new AiPathPoint
        {
            Position = Vector3.Zero,
            Action = AiPathPointAction.ReturnToCommandSet,
            Param = string.Empty
        });

        var ticks = 0;
        while (!ai.ReturnedToCommandSet && ticks < 2000)
        {
            ai.PathHandler.RunCurrentPath(TimeSpan.FromMilliseconds(100));
            ticks++;
        }

        var finalWaypoint = route[^1].Position;
        var position = npc.Transform.World.Position;

        await Assert.That(ai.ReturnedToCommandSet).IsTrue();
        await Assert.That(ai.PathHandler.AiPathPointsRemaining.Count).IsEqualTo(0);
        await Assert.That(MathF.Abs(position.X - finalWaypoint.X)).IsLessThan(1f);
        await Assert.That(MathF.Abs(position.Y - finalWaypoint.Y)).IsLessThan(1f);
        // Walked on the authored floor, not on the navigation node above it
        await Assert.That(MathF.Abs(position.Z - finalWaypoint.Z)).IsLessThan(0.05f);
    }

    [Test]
    public async Task RunCurrentPath_WalkSpeedRoute_BroadcastsWalkingMovement()
    {
        var (npc, ai) = CreateWalker();
        ai.LoadAiPathPoints(AlistairRoute, true);

        // First tick consumes the waypoint the NPC already stands on, the rest actually walk
        for (var i = 0; i < 10; i++)
            ai.PathHandler.RunCurrentPath(TimeSpan.FromMilliseconds(100));

        var move = npc.Movements[^1];
        // The route's authored Speed action is an ABSOLUTE pace in m/s (AiPathHandler assigns it
        // directly and GetRealMovementSpeed only applies MoveSpeedMul bonuses) — player-tuned to
        // 5.8 m/s. Gait must FOLLOW the speed: above walking pace derives run (4), not walk (5).
        // Expectations derive from the path file so pace retuning stays data-only.
        var authoredSpeed = float.Parse(
            Route.First(p => p.Action == AiPathPointAction.Speed).Param,
            System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(authoredSpeed).IsGreaterThan(1f);
        await Assert.That(ai.PathHandler.AiPathActorFlags).IsNull();
        await Assert.That(ai.PathHandler.AiPathSpeed).IsEqualTo(authoredSpeed);
        await Assert.That(move.ActorFlags).IsEqualTo((byte)4);
        await Assert.That(move.Flags.HasFlag(MoveTypeFlags.Moving)).IsTrue();
        await Assert.That(move.DeltaMovement[1]).IsEqualTo((sbyte)127);
        // authored m/s at 2048 wire units per m/s (probe NPC has no MoveSpeedMul bonus)
        var velocity = MathF.Sqrt((move.VelX * (float)move.VelX) + (move.VelY * (float)move.VelY));
        await Assert.That(velocity).IsEqualTo(2048f * authoredSpeed).Within(12f);
        // Descends towards the recorded floor instead of being pinned to the navigation node above it
        await Assert.That(npc.Transform.World.Position.Z).IsLessThan(NavigationZ);
        await Assert.That(npc.Transform.World.Position.Z).IsGreaterThanOrEqualTo(Route.Min(p => p.Position.Z));
    }

    [Test]
    public async Task FollowPathCommand_LoadsItsOwnPathFile()
    {
        var (npc, ai) = CreateWalker();
        // Stale state left behind by an earlier command / spawner route
        ai.AiFileName = OtherRoute;
        var behavior = new TestRunCommandSetBehavior { Ai = ai };
        ai.AiCommandsQueue.Enqueue(new AiCommands
        {
            CmdSetId = 185,
            CmdId = AiCommandCategory.FollowPath,
            Param1 = 1,
            Param2 = AlistairRoute
        });

        behavior.Tick(TimeSpan.FromMilliseconds(100));

        var expected = AiPathsManager.Instance.LoadAiPathPoints(AlistairRoute);
        var other = AiPathsManager.Instance.LoadAiPathPoints(OtherRoute);
        // Routes are distinguished by content, not count (both files may have the same row count).
        await Assert.That(expected[0].Position).IsNotEqualTo(other[0].Position);
        await Assert.That(ai.AiFileName).IsEqualTo(AlistairRoute);
        // Queued route points plus the ReturnToCommandSet marker that resumes the command set
        await Assert.That(ai.PathHandler.AiPathPointsRemaining.Count).IsEqualTo(expected.Count + 1);
        await Assert.That(ai.PathHandler.AiPathPointsRemaining.First().Position).IsEqualTo(expected[0].Position);
        await Assert.That(ai.PathHandler.AiPathPointsRemaining.Last().Action)
            .IsEqualTo(AiPathPointAction.ReturnToCommandSet);
        await Assert.That(ai.WentToFollowPath).IsTrue();
        await Assert.That(npc.ObjId).IsEqualTo(npc.ObjId);
    }

    [Test]
    public async Task FollowPathCommand_MissingPathFile_StaysInCommandSet()
    {
        var (_, ai) = CreateWalker();
        var behavior = new TestRunCommandSetBehavior { Ai = ai };
        ai.AiCommandsQueue.Enqueue(new AiCommands
        {
            CmdSetId = 185,
            CmdId = AiCommandCategory.FollowPath,
            Param1 = 1,
            Param2 = "aipath_that_does_not_exist"
        });

        behavior.Tick(TimeSpan.FromMilliseconds(100));

        await Assert.That(ai.WentToFollowPath).IsFalse();
        await Assert.That(ai.PathHandler.AiPathPointsRemaining.Count).IsEqualTo(0);
    }

    private static (MovementProbeNpc Npc, TestPathAi Ai) CreateWalker()
    {
        var template = CreateWorldTemplate(248);
        AddNetMissionBai(template, new Vector3(745f, 326f, NavigationZ), BaiNavigationType.WaypointHuman);

        var npc = new MovementProbeNpc
        {
            Hp = 100,
            // Keeps CheckMovedPosition out of the WorldManager region bookkeeping
            DisabledSetPosition = true,
            Template = new NpcTemplate { Id = 12108, Scale = 1f, ModelId = 11 }
        };
        var world = new WorldInstance(template, 1, true, 1);
        SetPrivateMember(npc, "_parentWorld", world, typeof(GameObject));

        var route = AiPathsManager.Instance.LoadAiPathPoints(AlistairRoute);
        var start = route[0].Position;
        // Spawned where retail stood it, but with the navigation node height the resolver reports
        npc.Transform.Local.SetPosition(start.X, start.Y, NavigationZ);

        var ai = new TestPathAi { Owner = npc };
        npc.Ai = ai;
        ai.HomePosition = npc.Transform.World.Position;
        ai.IdlePosition = ai.HomePosition;
        return (npc, ai);
    }

    private sealed class TestPathAi : NpcAi
    {
        public bool ReturnedToCommandSet { get; private set; }
        public bool WentToFollowPath { get; private set; }

        protected override void Build()
        {
        }

        public override void GoToRunCommandSet()
        {
            ReturnedToCommandSet = true;
        }

        public override void GoToFollowPath()
        {
            WentToFollowPath = true;
        }
    }

    private sealed class TestRunCommandSetBehavior : AAEmu.Game.Models.Game.AI.v2.Behaviors.Common.RunCommandSetBehavior
    {
    }

    private static WorldTemplate CreateWorldTemplate(ushort groundHeight)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "authored_path_route_test",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };

        var cell = new WorldCell(0, 0, template);
        SetPrivateMember(cell, nameof(WorldCell.HeightMap), CreateHeightMap(groundHeight));
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
