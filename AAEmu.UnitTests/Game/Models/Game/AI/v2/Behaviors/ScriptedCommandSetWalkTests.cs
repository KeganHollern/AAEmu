using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Controls;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.AI.v2.Params;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Behaviors;

/// <summary>
/// aaemu-cluster#92: end to end run of Allistair's retail command set 185 (dialogue skills, then
/// FollowPath aipath_alistair0_0, then the self-despawn skill 19430). The queue must stay the sequencer:
/// the walk starts only when FollowPath is dequeued, terminates, and hands control back so the last
/// UseSkill runs instead of leaving a permanent ghost NPC on the ledge.
/// </summary>
[NotInParallel]
public sealed class ScriptedCommandSetWalkTests
{
    private const uint CommandSetId = 185;
    private const string AlistairRoute = "aipath_alistair0_0";
    private const uint DespawnSkillId = 19430;
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

        var skillManager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetPrivateMember(skillManager, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        SetPrivateMember(skillManager, "_skills", new Dictionary<uint, SkillTemplate>());
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
    public async Task CommandSet_DialogueThenFollowPath_WalksOnceAndReachesDespawnSkill()
    {
        var npc = CreateWalker(out var ai);
        var route = AiPathsManager.Instance.LoadAiPathPoints(AlistairRoute);
        var startPosition = npc.Transform.World.Position;

        ai.EnqueueAiCommands(CreateCommandSet());

        // Dialogue beats first: the walk must not start while the skills are still being issued
        ai.Tick(TimeSpan.FromMilliseconds(100));
        await Assert.That(ai.GetCurrentBehavior()).IsTypeOf<RunCommandSetBehavior>();
        await Assert.That(npc.Movements.Count).IsEqualTo(0);
        await Assert.That(npc.Transform.World.Position).IsEqualTo(startPosition);

        var ticks = 0;
        while (ai.AiSkillId != DespawnSkillId && ticks < 4000)
        {
            ai.Tick(TimeSpan.FromMilliseconds(100));
            ticks++;
        }

        var finalWaypoint = route[^1].Position;
        var position = npc.Transform.World.Position;

        // Command set advanced all the way to the self-despawn skill
        await Assert.That(ai.AiSkillId).IsEqualTo(DespawnSkillId);
        await Assert.That(ai.IdleRequests).IsEqualTo(0);
        await Assert.That(ai.AiCommandsQueue.Count).IsEqualTo(0);
        // And it got there by actually walking the authored route to its end
        await Assert.That(npc.Movements.Count).IsGreaterThan(0);
        await Assert.That(MathF.Abs(position.X - finalWaypoint.X)).IsLessThan(1f);
        await Assert.That(MathF.Abs(position.Y - finalWaypoint.Y)).IsLessThan(1f);
        await Assert.That(MathF.Abs(position.Z - finalWaypoint.Z)).IsLessThan(0.05f);
        // Jogged (the route's Speed 2 action doubles the pace, so the derived gait is run),
        // relaxed, and with the moving flag set for the whole route
        var walkMove = npc.Movements[^1];
        await Assert.That(walkMove.ActorFlags).IsEqualTo((byte)4);
        await Assert.That(walkMove.Stance).IsEqualTo(GameStanceType.Relaxed);
        await Assert.That(walkMove.Alertness).IsEqualTo(MoveTypeAlertness.Idle);
    }

    private static List<AiCommands> CreateCommandSet()
    {
        // ai_commands rows for cmd_set_id 185
        return
        [
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.UseSkill, Param1 = 19425, Param2 = "0" },
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.Timeout, Param1 = 1, Param2 = "0" },
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.UseSkill, Param1 = 19426, Param2 = "0" },
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.Timeout, Param1 = 1, Param2 = "0" },
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.UseSkill, Param1 = 19427, Param2 = "0" },
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.Timeout, Param1 = 1, Param2 = "0" },
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.FollowPath, Param1 = 1, Param2 = AlistairRoute },
            new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.UseSkill, Param1 = DespawnSkillId, Param2 = "0" }
        ];
    }

    private static MovementProbeNpc CreateWalker(out ScriptedWalkAi ai)
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
        SetPrivateMember(npc, "_parentWorld", world, typeof(GameObject), true);

        var route = AiPathsManager.Instance.LoadAiPathPoints(AlistairRoute);
        var start = route[0].Position;
        npc.Transform.Local.SetPosition(start.X, start.Y, NavigationZ);

        ai = new ScriptedWalkAi { Owner = npc };
        npc.Ai = ai;
        ai.HomePosition = npc.Transform.World.Position;
        ai.IdlePosition = ai.HomePosition;
        ai.Start();
        return npc;
    }

    private sealed class ScriptedWalkAi : NpcAi
    {
        public int IdleRequests { get; private set; }

        protected override void Build()
        {
            AddBehavior(BehaviorKind.RunCommandSet, new RunCommandSetBehavior());
            AddBehavior(BehaviorKind.FollowPath, new FollowPathBehavior());
        }

        public override void GoToIdle()
        {
            IdleRequests++;
            base.GoToIdle();
        }
    }

    private static WorldTemplate CreateWorldTemplate(ushort groundHeight)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "scripted_command_set_walk_test",
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

    private static void SetPrivateMember(object target, string name, object value, Type declaringType = null,
        bool fieldOnly = false)
    {
        var type = declaringType ?? target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        if (fieldOnly)
            throw new MissingFieldException(type.FullName, name);

        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }
}
