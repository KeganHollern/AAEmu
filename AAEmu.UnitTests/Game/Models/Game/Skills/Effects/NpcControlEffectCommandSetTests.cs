using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.AI.v2.Params;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects;

/// <summary>
/// aaemu-cluster#92: RunCommandSet must not start the route walk at Apply time when the commands were
/// queued on the AI - the queue is the sequencer and issues FollowPath at the right beat. Allistair's
/// retail set 185 (three dialogue skills on 1s beats, then FollowPath, then the despawn skill) walked
/// during his first line because both drivers ran at once.
/// </summary>
[NotInParallel]
public sealed class NpcControlEffectCommandSetTests
{
    private const uint CommandSetId = 185;
    private const string AlistairRoute = "aipath_alistair0_0";

    private IServiceProvider _previousServiceProvider;
    private ServiceProvider _testServiceProvider;

    [Before(Test)]
    public void SetUp()
    {
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        var services = new ServiceCollection();

        var aiGameData = new AiGameData();
        SetPrivateMember(aiGameData, "_aiCommands", new Dictionary<uint, List<AiCommands>>
        {
            [CommandSetId] =
            [
                new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.UseSkill, Param1 = 19425, Param2 = "0" },
                new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.Timeout, Param1 = 1, Param2 = "0" },
                new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.FollowPath, Param1 = 1, Param2 = AlistairRoute },
                new AiCommands { CmdSetId = CommandSetId, CmdId = AiCommandCategory.UseSkill, Param1 = 19430, Param2 = "0" }
            ]
        });
        services.AddSingleton(aiGameData);

        // Simulation re-schedules its next step through the TaskManager queue
        services.AddSingleton(new TaskManager(Mock.Of<ITickManager>().Object));

        _testServiceProvider = services.BuildServiceProvider();
        SingletonContainer.ServiceProvider = _testServiceProvider;
    }

    [After(Test)]
    public void TearDown()
    {
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        ResetSingleton<AiGameData>();
        ResetSingleton<TaskManager>();
        _testServiceProvider?.Dispose();
    }

    [Test]
    public async Task Apply_RunCommandSet_WithAi_QueuesCommandsWithoutStartingTheWalk()
    {
        var npc = CreateNpc();
        var ai = new TestAi { Owner = npc };
        npc.Ai = ai;
        var effect = new NpcControlEffect
        {
            CategoryId = NpcControlCategory.RunCommandSet,
            ParamString = string.Empty,
            ParamInt = CommandSetId
        };

        effect.Apply(npc, null, npc, null, null, null, null, DateTime.UtcNow);

        await Assert.That(ai.AiCommandsQueue.Count).IsEqualTo(4);
        await Assert.That(ai.WentToRunCommandSet).IsTrue();
        // The legacy Simulation walk stays untouched: no parallel route drive, no premature movement
        await Assert.That(npc.Simulation.MoveToPathEnabled).IsFalse();
        await Assert.That(npc.Simulation.MoveFileName).IsEmpty();
        await Assert.That(npc.IsInPatrol).IsFalse();
        await Assert.That(npc.Movements.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_RunCommandSet_WithoutAi_StartsTheWalkImmediately()
    {
        var npc = CreateNpc();
        var effect = new NpcControlEffect
        {
            CategoryId = NpcControlCategory.RunCommandSet,
            ParamString = string.Empty,
            ParamInt = CommandSetId
        };

        effect.Apply(npc, null, npc, null, null, null, null, DateTime.UtcNow);

        // Nothing can sequence the set, so the pre-scanned route still runs right away
        await Assert.That(npc.Simulation.MoveFileName).IsEqualTo(AlistairRoute);
        await Assert.That(npc.Simulation.MoveToPathEnabled).IsTrue();
        await Assert.That(npc.IsInPatrol).IsTrue();
    }

    private static MovementProbeNpc CreateNpc()
    {
        var npc = new MovementProbeNpc
        {
            Hp = 100,
            DisabledSetPosition = true,
            Template = new NpcTemplate { Id = 12108, Scale = 1f, ModelId = 11 }
        };
        npc.Transform.Local.SetPosition(749.7f, 325.8f, 248.2f);
        npc.Simulation = new Simulation(npc);
        return npc;
    }

    private sealed class TestAi : NpcAi
    {
        public bool WentToRunCommandSet { get; private set; }

        protected override void Build()
        {
        }

        public override void GoToRunCommandSet()
        {
            WentToRunCommandSet = true;
        }
    }

    private static void ResetSingleton<T>() where T : class
    {
        typeof(Singleton<T>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, null);
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
