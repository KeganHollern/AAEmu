using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class DoodadPhaseCycleTests
{
    private const uint FirstPhase = 1404;
    private const uint SecondPhase = 1405;
    private const uint EveningFirstPhase = 1531;
    private const uint EveningSecondPhase = 1752;
    private const uint BrazierMorningPhase = 6306;
    private const uint BrazierIntermediatePhase = 6307;
    private const uint BrazierEveningPhase = 6373;
    private static readonly FieldInfo s_managerInstanceField =
        typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private DoodadManager _previousManager;
    private RedirectPhaseFunc _firstRedirect;
    private RedirectPhaseFunc _secondRedirect;
    private RecordingPhaseFunc _recordingPhaseFunc;

    [Before(Test)]
    public void SetUp()
    {
        _previousManager = (DoodadManager)s_managerInstanceField.GetValue(null);
        var manager = new DoodadManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);

        _firstRedirect = new RedirectPhaseFunc(SecondPhase) { Id = 1 };
        _secondRedirect = new RedirectPhaseFunc(FirstPhase) { Id = 2 };
        var eveningRedirect = new DoodadFuncTod
        {
            Id = 3,
            Tod = 1800,
            TodAsHours = 18f,
            NextPhase = (int)EveningSecondPhase
        };
        var morningRedirect = new DoodadFuncTod
        {
            Id = 4,
            Tod = 600,
            TodAsHours = 6f,
            NextPhase = (int)EveningFirstPhase
        };
        var brazierMorningRedirect = new DoodadFuncTod
        {
            Id = 5,
            Tod = 400,
            TodAsHours = 4f,
            NextPhase = (int)BrazierIntermediatePhase
        };
        var brazierEveningRedirect = new DoodadFuncTod
        {
            Id = 6,
            Tod = 2000,
            TodAsHours = 20f,
            NextPhase = (int)BrazierEveningPhase
        };
        var brazierStableRedirect = new DoodadFuncTod
        {
            Id = 7,
            Tod = 2000,
            TodAsHours = 20f,
            NextPhase = (int)BrazierMorningPhase
        };
        _recordingPhaseFunc = new RecordingPhaseFunc { Id = 8 };
        SetPrivateField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>());
        SetPrivateField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [FirstPhase] =
            [
                new DoodadPhaseFunc
                {
                    GroupId = FirstPhase,
                    FuncId = _firstRedirect.Id,
                    FuncType = nameof(RedirectPhaseFunc)
                }
            ],
            [SecondPhase] =
            [
                new DoodadPhaseFunc
                {
                    GroupId = SecondPhase,
                    FuncId = _secondRedirect.Id,
                    FuncType = nameof(RedirectPhaseFunc)
                }
            ],
            [EveningFirstPhase] =
            [
                new DoodadPhaseFunc
                {
                    GroupId = EveningFirstPhase,
                    FuncId = eveningRedirect.Id,
                    FuncType = nameof(DoodadFuncTod)
                }
            ],
            [EveningSecondPhase] =
            [
                new DoodadPhaseFunc
                {
                    GroupId = EveningSecondPhase,
                    FuncId = morningRedirect.Id,
                    FuncType = nameof(DoodadFuncTod)
                },
                new DoodadPhaseFunc
                {
                    GroupId = EveningSecondPhase,
                    FuncId = _recordingPhaseFunc.Id,
                    FuncType = nameof(RecordingPhaseFunc)
                }
            ],
            [BrazierMorningPhase] =
            [
                new DoodadPhaseFunc
                {
                    GroupId = BrazierMorningPhase,
                    FuncId = brazierMorningRedirect.Id,
                    FuncType = nameof(DoodadFuncTod)
                },
                new DoodadPhaseFunc
                {
                    GroupId = BrazierMorningPhase,
                    FuncId = _recordingPhaseFunc.Id,
                    FuncType = nameof(RecordingPhaseFunc)
                }
            ],
            [BrazierIntermediatePhase] =
            [
                new DoodadPhaseFunc
                {
                    GroupId = BrazierIntermediatePhase,
                    FuncId = brazierEveningRedirect.Id,
                    FuncType = nameof(DoodadFuncTod)
                }
            ],
            [BrazierEveningPhase] =
            [
                new DoodadPhaseFunc
                {
                    GroupId = BrazierEveningPhase,
                    FuncId = brazierStableRedirect.Id,
                    FuncType = nameof(DoodadFuncTod)
                }
            ]
        });
        SetPrivateField(manager, "_phaseFuncTemplates",
            new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
            {
                [nameof(RedirectPhaseFunc)] = new Dictionary<uint, DoodadPhaseFuncTemplate>
                {
                    [_firstRedirect.Id] = _firstRedirect,
                    [_secondRedirect.Id] = _secondRedirect
                },
                [nameof(DoodadFuncTod)] = new Dictionary<uint, DoodadPhaseFuncTemplate>
                {
                    [eveningRedirect.Id] = eveningRedirect,
                    [morningRedirect.Id] = morningRedirect,
                    [brazierMorningRedirect.Id] = brazierMorningRedirect,
                    [brazierEveningRedirect.Id] = brazierEveningRedirect,
                    [brazierStableRedirect.Id] = brazierStableRedirect
                },
                [nameof(RecordingPhaseFunc)] = new Dictionary<uint, DoodadPhaseFuncTemplate>
                {
                    [_recordingPhaseFunc.Id] = _recordingPhaseFunc
                }
            });
        s_managerInstanceField.SetValue(null, manager);
    }

    [After(Test)]
    public void TearDown()
    {
        s_managerInstanceField.SetValue(null, _previousManager);
    }

    [Test]
    public async Task DoChangePhase_EmptyFuncCycle_StopsAtRepeatedPhaseOnEveryCall()
    {
        var doodad = new Doodad
        {
            TemplateId = 1121,
            Template = new DoodadTemplate { Id = 1121 }
        };

        var firstStopped = doodad.DoChangePhase(null, (int)FirstPhase);
        var secondStopped = doodad.DoChangePhase(null, (int)FirstPhase);

        await Assert.That(firstStopped).IsTrue();
        await Assert.That(secondStopped).IsTrue();
        await Assert.That(doodad.FuncGroupId).IsEqualTo(FirstPhase);
        await Assert.That(_firstRedirect.Calls).IsEqualTo(2);
        await Assert.That(_secondRedirect.Calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(2f, EveningSecondPhase)]
    [Arguments(10f, EveningFirstPhase)]
    [Arguments(22f, EveningSecondPhase)]
    public async Task ResolveTodPhase_SelectsTargetOfLatestDailyEdge(float currentHour, uint expectedPhase)
    {
        var doodad = new Doodad
        {
            TemplateId = 1032,
            Template = new DoodadTemplate { Id = 1032, ForceTodTopPriority = true }
        };

        var phase = doodad.ResolveTodPhase(EveningFirstPhase, currentHour);

        await Assert.That(phase).IsEqualTo(expectedPhase);
    }

    [Test]
    public async Task ResolveTodPhase_WithoutTopPriority_KeepsStartPhase()
    {
        var doodad = new Doodad
        {
            TemplateId = 1032,
            Template = new DoodadTemplate { Id = 1032, ForceTodTopPriority = false }
        };

        var phase = doodad.ResolveTodPhase(EveningFirstPhase, 22f);

        await Assert.That(phase).IsEqualTo(EveningFirstPhase);
    }

    [Test]
    public async Task ResolveTodPhase_SameTimeChain_SelectsLastStableTarget()
    {
        var doodad = new Doodad
        {
            TemplateId = 2842,
            Template = new DoodadTemplate { Id = 2842, ForceTodTopPriority = true }
        };

        var phase = doodad.ResolveTodPhase(BrazierMorningPhase, 22f);

        await Assert.That(phase).IsEqualTo(BrazierMorningPhase);
    }

    [Test]
    public async Task DoChangePhase_WithTodOverrideSuppressed_KeepsRequestedPhase()
    {
        var doodad = new Doodad
        {
            TemplateId = 1032,
            Template = new DoodadTemplate { Id = 1032, ForceTodTopPriority = true }
        };

        var stopped = doodad.ApplyTodPhase(null, (int)EveningSecondPhase);

        await Assert.That(stopped).IsFalse();
        await Assert.That(doodad.FuncGroupId).IsEqualTo(EveningSecondPhase);
        await Assert.That(_recordingPhaseFunc.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task TimeManager_CrossedSameTimeChain_SetsStableTarget()
    {
        var doodad = new Doodad
        {
            ObjId = 1,
            TemplateId = 2842,
            Template = new DoodadTemplate { Id = 2842, ForceTodTopPriority = true }
        };
        doodad.FuncGroupId = BrazierIntermediatePhase;

        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        world.AddObject(doodad);
        var worldManager = Mock.Of<IWorldManager>();
        worldManager.GetWorlds().Returns([world]);
        var options = Options.Create(new AppConfiguration { World = new WorldConfig() });
        var timeManager = new TimeManager(
            Mock.Of<ITickManager>().Object,
            worldManager.Object,
            TimeProvider.System,
            options);

        timeManager.OnTimeOfDayChange(20.01f, 19.99f);

        await Assert.That(doodad.FuncGroupId).IsEqualTo(BrazierMorningPhase);
        await Assert.That(_recordingPhaseFunc.Calls).IsEqualTo(1);
        await Assert.That(doodad.CurrentToDTriggers[4f]).IsEqualTo((int)BrazierIntermediatePhase);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private sealed class RedirectPhaseFunc(int nextPhase) : DoodadPhaseFuncTemplate
    {
        public int Calls { get; private set; }

        public override bool Use(BaseUnit caster, Doodad owner)
        {
            Calls++;
            if (Calls > 2)
                throw new InvalidOperationException("The phase cycle did not stop.");

            owner.OverridePhase = nextPhase;
            return true;
        }
    }

    private sealed class RecordingPhaseFunc : DoodadPhaseFuncTemplate
    {
        public int Calls { get; private set; }

        public override bool Use(BaseUnit caster, Doodad owner)
        {
            Calls++;
            return false;
        }
    }
}
