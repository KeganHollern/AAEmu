using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Tasks.Doodads;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PaidPhaseChange_DueGrowthCallbackWaitsForCommitOutcome(bool succeeds)
    {
        SetInstance(new TaskManager(null));
        var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            ItemManager.Instance, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object), Mock.Of<ISusManager>().Object);
        SetInstance(manager);
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>());
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());

        // Player plants have no spawner. Template 322 grows from phase 233 to 234,
        // while the paid uproot interaction leaves phase 233 for phase 8089.
        // Only those competing transitions are needed here, not the later harvest branches.
        var doodad = new LaborTaskDoodad
        {
            TemplateId = 322, Template = new DoodadTemplate { Id = 322 }, FuncGroupId = 233
        };
        using var dispatched = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        var growth = new DispatchedGrowthTask(doodad, dispatched, completed);
        doodad.FuncTask = growth;
        TaskManager.Instance.Schedule(growth, TimeSpan.FromMilliseconds(-1));

        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => succeeds;
        var finishedBeforeSettlement = false;
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            doodad.DoChangePhase(_owner, 8089);
            // Use the real runner: it removes a one-shot task as it dispatches the callback.
            typeof(TaskManager).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(TaskManager.Instance, [TimeSpan.Zero]);
            if (!dispatched.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The due growth callback did not start.");
            if (!SpinWait.SpinUntil(() => completed.IsSet ||
                    (growth.CallbackThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The growth callback neither waited nor completed.");
            finishedBeforeSettlement = completed.IsSet;
        });

        if (!completed.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The growth callback did not finish after settlement.");

        await Assert.That(growth.Exception).IsNull();
        await Assert.That(finishedBeforeSettlement).IsFalse();
        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(doodad.Spawner).IsNull();
        await Assert.That(doodad.FuncGroupId).IsEqualTo(succeeds ? 8089u : 234u);
        await Assert.That(growth.Effects).IsEqualTo(succeeds ? 0 : 1);
        await Assert.That(growth.Retirements).IsEqualTo(succeeds ? 1 : 0);
        await Assert.That(doodad.FuncTask).IsNull();
        await Assert.That(TaskManager.Instance.GetQueueCount()).IsEqualTo(0);
        await Assert.That(_owner.LaborPower).IsEqualTo(succeeds ? (short)10 : (short)20);
    }

    private sealed class LaborTaskDoodad : Doodad
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class DispatchedGrowthTask(Doodad owner, ManualResetEventSlim dispatched, ManualResetEventSlim completed)
        : DoodadFuncGrowthTask(null, owner, 0, 234, 1f)
    {
        public Thread CallbackThread { get; private set; }
        public Exception Exception { get; private set; }
        public int Effects { get; private set; }
        public int Retirements { get; private set; }

        public override Task ExecuteAsync()
        {
            CallbackThread = Thread.CurrentThread;
            dispatched.Set();
            try
            {
                Execute();
            }
            catch (Exception exception)
            {
                Exception = exception;
            }
            finally
            {
                completed.Set();
            }
            return Task.CompletedTask;
        }

        protected override void ExecuteCurrent()
        {
            Effects++;
            base.ExecuteCurrent();
        }

        protected override void OnRetired() => Retirements++;
    }
}
