using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.v2.Controls;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Units.Route;

/// <summary>
/// aaemu-cluster#92: the legacy Simulation route driver (NpcControlEffect category 2 "FollowPath" and the
/// /moveto command) has to keep the authored waypoint height and broadcast a real walk.
/// </summary>
[NotInParallel]
public sealed class SimulationScriptedWalkTests
{
    private const string AlistairRoute = "aipath_alistair0_0";
    /// <summary>How far above the recorded floor the indoor navigation node sits</summary>
    private const float NavigationOffset = 0.7f;

    private static List<AiPathPoint> Route => AiPathsManager.Instance.LoadAiPathPoints(AlistairRoute);
    private static float NavigationZ => Route[0].Position.Z + NavigationOffset;

    private IServiceProvider _previousServiceProvider;
    private ServiceProvider _testServiceProvider;

    [Before(Test)]
    public void SetUp()
    {
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        var services = new ServiceCollection();

        // Npc.BaseMoveSpeed asks for the actor model (none here: falls back to 1 m/s, a walk)
        var modelManager = new ModelManager();
        SetPrivateMember(modelManager, "_modelTypes", new Dictionary<uint, ModelType>());
        SetPrivateMember(modelManager, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        services.AddSingleton(modelManager);

        // Simulation re-schedules its next step through the TaskManager queue
        services.AddSingleton(new TaskManager(Mock.Of<ITickManager>().Object));

        _testServiceProvider = services.BuildServiceProvider();
        SingletonContainer.ServiceProvider = _testServiceProvider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        ResetSingleton<ModelManager>();
        ResetSingleton<TaskManager>();
        _testServiceProvider?.Dispose();
    }

    [Test]
    public async Task MoveTo_NavigationSourcedFloor_KeepsAuthoredHeightAndBroadcastsWalk()
    {
        var npc = CreateWalker();
        var simulation = new Simulation(npc) { MoveToPathEnabled = true };
        var target = Route[^1].Position;

        simulation.MoveTo(simulation, npc, target);

        var move = npc.Movements.Single();
        // Server no longer re-asserts the navigation node height every step
        await Assert.That(npc.Transform.World.Position.Z).IsLessThan(NavigationZ);
        await Assert.That(move.Z).IsEqualTo(npc.Transform.World.Position.Z);
        // Walk gait, moving flag and full forward throttle, so the client plays a walk cycle
        await Assert.That(move.ActorFlags).IsEqualTo((byte)5);
        await Assert.That(move.Flags.HasFlag(MoveTypeFlags.Moving)).IsTrue();
        await Assert.That(move.DeltaMovement[1]).IsEqualTo((sbyte)127);
        // 1 m/s at 2048 wire units per m/s, instead of the hardcoded ~2 m/s
        var velocity = MathF.Sqrt((move.VelX * (float)move.VelX) + (move.VelY * (float)move.VelY));
        await Assert.That(velocity).IsEqualTo(2048f).Within(2f);
    }

    [Test]
    public async Task MoveTo_ReachedWaypointWithVerticalOffset_AdvancesInsteadOfStalling()
    {
        var npc = CreateWalker();
        var simulation = new Simulation(npc) { MoveToPathEnabled = true, MoveFileName = AlistairRoute };
        // Standing on the last waypoint, but 0.7m above it: a 3D range test never accepted this as arrival
        var lastWaypoint = Route[^1].Position;
        npc.Transform.Local.SetPosition(lastWaypoint.X, lastWaypoint.Y, lastWaypoint.Z + NavigationOffset);

        simulation.MoveTo(simulation, npc, lastWaypoint);

        // Arrival accepted: the route advanced (and, with no loaded route left, ended) instead of
        // broadcasting yet another step towards a waypoint it can never reach in 3D
        await Assert.That(npc.Movements.Any(m => m.Flags.HasFlag(MoveTypeFlags.Moving))).IsFalse();
        await Assert.That(simulation.MoveToPathEnabled).IsFalse();
    }

    [Test]
    public async Task GoToPath_OutOfBattle_WalksInRelaxedStance()
    {
        var npc = CreateWalker();
        npc.CurrentGameStance = GameStanceType.Combat;
        npc.CurrentAlertness = MoveTypeAlertness.Combat;
        var simulation = new Simulation(npc) { MoveFileName = AlistairRoute, MoveToPathEnabled = false };

        simulation.GoToPath(npc, true);

        await Assert.That(simulation.MoveToPathEnabled).IsTrue();
        await Assert.That(npc.CurrentGameStance).IsEqualTo(GameStanceType.Relaxed);
        await Assert.That(npc.CurrentAlertness).IsEqualTo(MoveTypeAlertness.Idle);
    }

    private static MovementProbeNpc CreateWalker()
    {
        var template = CreateWorldTemplate(248);
        AddNetMissionBai(template, new Vector3(745f, 326f, NavigationZ), BaiNavigationType.WaypointHuman);

        var npc = new MovementProbeNpc
        {
            Hp = 100,
            DisabledSetPosition = true,
            Template = new NpcTemplate { Id = 12108, Scale = 1f, ModelId = 11 }
        };
        var world = new WorldInstance(template, 1, true, 1);
        SetPrivateMember(npc, "_parentWorld", world, typeof(GameObject));
        var start = Route[0].Position;
        npc.Transform.Local.SetPosition(start.X, start.Y, NavigationZ);

        var ai = new TestPathAi { Owner = npc };
        npc.Ai = ai;
        ai.HomePosition = npc.Transform.World.Position;
        ai.IdlePosition = ai.HomePosition;
        return npc;
    }

    private sealed class TestPathAi : NpcAi
    {
        protected override void Build()
        {
        }
    }

    private static WorldTemplate CreateWorldTemplate(ushort groundHeight)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "simulation_scripted_walk_test",
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

    private static void ResetSingleton<T>() where T : class
    {
        typeof(Singleton<T>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, null);
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
