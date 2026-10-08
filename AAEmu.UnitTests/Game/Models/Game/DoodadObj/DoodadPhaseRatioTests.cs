using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class DoodadPhaseRatioTests
{
    private static readonly FieldInfo s_managerInstance = typeof(Singleton<DoodadManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo s_phaseRatio = typeof(Doodad).GetProperty(nameof(Doodad.PhaseRatio))!;
    private object _previousManager;
    private readonly Dictionary<uint, List<DoodadPhaseFunc>> _phases = [];
    private readonly Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>> _functions = [];
    private uint _nextFunctionId;

    [Before(Test)]
    public void SetUp()
    {
        _previousManager = s_managerInstance.GetValue(null);
        var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);
        SetField(manager, "_phaseFuncs", _phases);
        SetField(manager, "_phaseFuncTemplates", _functions);
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>());
        SetField(manager, "_templates", new Dictionary<uint, DoodadTemplate>
        {
            [3085] = new() { Id = 3085 }
        });
        s_managerInstance.SetValue(null, manager);
    }

    [After(Test)]
    public void TearDown() => s_managerInstance.SetValue(null, _previousManager);

    [Test]
    [Arguments("exact")]
    [Arguments("partial")]
    [Arguments("weather_7055")]
    [Arguments("partial_7286")]
    [Arguments("overflow_8000")]
    [Arguments("overflow_5000")]
    [Arguments("overflow_50000")]
    [Arguments("numeric_limits")]
    public async Task OrderedWeights_AllTenThousandRollsHaveTheApprovedDistribution(string scenario)
    {
        var (weights, expected, expectedUnselected) = scenario switch
        {
            "exact" => (new[] { 2000, 3000, 5000 }, new[] { 2000, 3000, 5000 }, 0),
            "partial" => (new[] { 2000, 3000 }, new[] { 2000, 3000 }, 5000),
            "weather_7055" => (new[] { 2000, 2000, 2000, 2000, 2000 },
                new[] { 2000, 2000, 2000, 2000, 2000 }, 0),
            "partial_7286" => (new[] { 3300, 3300, 3300 }, new[] { 3300, 3300, 3300 }, 100),
            "overflow_8000" => (new[] { 8000, 8000 }, new[] { 8000, 2000 }, 0),
            "overflow_5000" => (new[] { 5000, 5000, 5000 }, new[] { 5000, 5000, 0 }, 0),
            "overflow_50000" => (new[] { 50000, 50000 }, new[] { 10000, 0 }, 0),
            "numeric_limits" => (new[] { 0, -1, int.MinValue, int.MaxValue, int.MaxValue },
                new[] { 0, 0, 0, 10000, 0 }, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var selected = new int[weights.Length];
        var unselected = 0;
        var owner = new Doodad();
        for (var roll = 0; roll < 10000; roll++)
        {
            s_phaseRatio.SetValue(owner, roll);
            owner.CumulativePhaseRatio = 0;
            var hits = 0;
            for (var row = 0; row < weights.Length; row++)
            {
                if (!owner.TrySelectPhaseRatio(weights[row]))
                    continue;
                selected[row]++;
                hits++;
            }
            if (hits == 0)
                unselected++;
            // Even if the caller examines later rows, intervals must never overlap.
            if (hits > 1)
                throw new InvalidOperationException($"Roll {roll} selected {hits} rows in {scenario}.");
        }
        await Assert.That(selected.SequenceEqual(expected)).IsTrue();
        await Assert.That(unselected).IsEqualTo(expectedUnselected);
        await Assert.That(owner.CumulativePhaseRatio).IsEqualTo(expected.Sum());
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1999, 0)]
    [Arguments(2000, 1)]
    [Arguments(4999, 1)]
    [Arguments(5000, 2)]
    [Arguments(9999, 2)]
    public async Task RatioChange_ExactBoundariesSelectOneAuthoredTarget(int roll, int expectedRow)
    {
        var owner = new Doodad();
        s_phaseRatio.SetValue(owner, roll);
        var weights = new[] { 2000, 3000, 5000 };
        var selected = -1;
        for (var row = 0; row < weights.Length; row++)
        {
            var function = new DoodadFuncRatioChange { Ratio = weights[row], NextPhase = 20 + row };
            if (!function.Use(null, owner))
                continue;
            selected = row;
            break;
        }
        await Assert.That(selected).IsEqualTo(expectedRow);
        await Assert.That(owner.OverridePhase).IsEqualTo(20 + expectedRow);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task RatioChange_NonpositiveWeightDoesNotSelectOrConsumeProbability(int weight)
    {
        var owner = new Doodad { CumulativePhaseRatio = 4000 };
        s_phaseRatio.SetValue(owner, 4000);
        var function = new DoodadFuncRatioChange { Ratio = weight, NextPhase = 20 };
        await Assert.That(function.Use(null, owner)).IsFalse();
        await Assert.That(owner.CumulativePhaseRatio).IsEqualTo(4000);
        await Assert.That(owner.OverridePhase).IsEqualTo(0);
    }

    [Test]
    public async Task PhaseFunctions_InterleavedNonratioFunctionsKeepOneRollAndTheCurrentInterval()
    {
        var observations = new List<(int Roll, int Used)>();
        Add(10, Observe(observations));
        Add(10, new DoodadFuncRatioChange { Ratio = 1000, NextPhase = 20 });
        Add(10, Observe(observations));
        Add(10, new DoodadFuncRatioChange { Ratio = 3000, NextPhase = 30 });
        Add(10, Observe(observations));
        var owner = new FixedRollDoodad(3500);

        owner.DoChangePhase(null, 10);

        await Assert.That(owner.RollCalls).IsEqualTo(1);
        await Assert.That(owner.FuncGroupId).IsEqualTo(30u);
        await Assert.That(observations.SequenceEqual(new[] { (3500, 0), (3500, 1000) })).IsTrue();
    }

    [Test]
    public async Task PhaseTransition_ResetsTheIntervalAndRollsAgainForTheDestinationPhase()
    {
        var observations = new List<(int Roll, int Used)>();
        Add(10, new DoodadFuncRatioChange { Ratio = 4000, NextPhase = 30 });
        Add(10, new DoodadFuncRatioChange { Ratio = 6000, NextPhase = 20 });
        Add(20, Observe(observations));
        Add(20, new DoodadFuncRatioChange { Ratio = 8000, NextPhase = 30 });
        var owner = new FixedRollDoodad(5000, 7777);

        owner.DoChangePhase(null, 10);

        await Assert.That(owner.RollCalls).IsEqualTo(2);
        await Assert.That(owner.FuncGroupId).IsEqualTo(30u);
        await Assert.That(observations.SequenceEqual(new[] { (7777, 0) })).IsTrue();
    }

    [Test]
    public async Task RepeatedPhaseEvaluation_ResetsTheIntervalAndUsesANewRoll()
    {
        var observations = new List<(int Roll, int Used)>();
        Add(10, new DoodadFuncRatioChange { Ratio = 2000, NextPhase = 20 });
        Add(10, Observe(observations));
        var owner = new FixedRollDoodad(8000, 1000);

        owner.DoChangePhase(null, 10);
        await Assert.That(owner.FuncGroupId).IsEqualTo(10u);
        await Assert.That(observations.SequenceEqual(new[] { (8000, 2000) })).IsTrue();
        owner.DoChangePhase(null, 10);

        await Assert.That(owner.RollCalls).IsEqualTo(2);
        await Assert.That(owner.FuncGroupId).IsEqualTo(20u);
        await Assert.That(owner.CumulativePhaseRatio).IsEqualTo(2000);
    }

    [Test]
    [Arguments(1999, true)]
    [Arguments(2000, false)]
    [Arguments(9999, false)]
    public async Task RatioRespawnAndRatioChange_ShareTheSamePhaseIntervals(int roll, bool respawnExpected)
    {
        var spawner = new RecordingSpawner { Id = 1 };
        Add(10, new DoodadFuncRatioRespawn { Ratio = 2000, SpawnDoodadId = 3085 });
        Add(10, new DoodadFuncRatioChange { Ratio = 8000, NextPhase = 20 });
        var owner = new FixedRollDoodad(roll) { Spawner = spawner };

        owner.DoChangePhase(null, 10);

        await Assert.That(owner.RollCalls).IsEqualTo(1);
        await Assert.That(spawner.SpawnCalls).IsEqualTo(respawnExpected ? 1 : 0);
        await Assert.That(owner.FuncGroupId).IsEqualTo(respawnExpected ? 10u : 20u);
        await Assert.That(spawner.SelectedTemplate).IsEqualTo(respawnExpected ? 3085u : 0u);
    }

    [Test]
    public async Task UnallocatedRoll_DoesNotStopALaterNonratioFunction()
    {
        var observations = new List<(int Roll, int Used)>();
        Add(10, new DoodadFuncRatioChange { Ratio = 2000, NextPhase = 20 });
        Add(10, new DoodadFuncRatioRespawn { Ratio = 3000, SpawnDoodadId = 3085 });
        Add(10, Observe(observations));
        var owner = new FixedRollDoodad(5000);

        await Assert.That(owner.DoChangePhase(null, 10)).IsFalse();
        await Assert.That(owner.FuncGroupId).IsEqualTo(10u);
        await Assert.That(owner.RollCalls).IsEqualTo(1);
        await Assert.That(observations.SequenceEqual(new[] { (5000, 5000) })).IsTrue();
    }

    [Test]
    public async Task EmptyPhase_DoesNotRequestARoll()
    {
        var owner = new FixedRollDoodad();
        await Assert.That(owner.DoChangePhase(null, 10)).IsFalse();
        await Assert.That(owner.RollCalls).IsEqualTo(0);
    }

    private void Add(uint phase, DoodadPhaseFuncTemplate function)
    {
        var type = function.GetType().Name;
        var id = ++_nextFunctionId;
        if (!_functions.TryGetValue(type, out var functions))
            _functions[type] = functions = [];
        functions[id] = function;
        if (!_phases.TryGetValue(phase, out var phaseFunctions))
            _phases[phase] = phaseFunctions = [];
        phaseFunctions.Add(new DoodadPhaseFunc { GroupId = phase, FuncId = id, FuncType = type });
    }

    private static RecordingPhase Observe(List<(int Roll, int Used)> observations) => new(owner =>
        observations.Add((owner.PhaseRatio, owner.CumulativePhaseRatio)));

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class FixedRollDoodad(params int[] rolls) : Doodad
    {
        public int RollCalls { get; private set; }
        protected override int RollPhaseRatio() => rolls[RollCalls++];
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class RecordingPhase(Action<Doodad> action) : DoodadPhaseFuncTemplate
    {
        public override bool Use(BaseUnit caster, Doodad owner)
        {
            action(owner);
            return false;
        }
    }

    private sealed class RecordingSpawner : DoodadSpawner
    {
        public int SpawnCalls { get; private set; }
        public uint SelectedTemplate { get; private set; }
        public override void Despawn(Doodad doodad) { }
        public override Doodad Spawn(uint objId)
        {
            SpawnCalls++;
            SelectedTemplate = RespawnDoodadTemplateId;
            return new Doodad { TemplateId = RespawnDoodadTemplateId };
        }
    }
}
