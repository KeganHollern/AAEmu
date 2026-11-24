using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class DoodadRatioSelfTransitionTests
{
    private const uint InitialPhase = 7079;
    private const uint OpenPhase = 7080;
    private const uint InteractionSkill = 14591;
    private readonly Dictionary<FieldInfo, object> _previous = [];

    [Before(Test)]
    public void SetUp()
    {
        var manager = new DoodadManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);

        // Use the production loader. Type-specific IDs and phase row IDs put the timer in different places.
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        using var compact = new SqliteConnection("Data Source=:memory:");
        compact.Open();
        using var insert = compact.CreateCommand();
        insert.CommandText = """
            CREATE TABLE doodad_phase_funcs(id INTEGER PRIMARY KEY, doodad_func_group_id INT, actual_func_id INT, actual_func_type TEXT);
            INSERT INTO doodad_phase_funcs VALUES
                (7223,7080,1388,'DoodadFuncFinal'), (7224,7079,1648,'DoodadFuncTimer'),
                (7283,7079,169,'DoodadFuncRatioChange'), (7284,7079,170,'DoodadFuncRatioChange'),
                (1,555,50,'DoodadFuncTimer'), (2,555,10,'DoodadFuncRatioChange');
            """;
        insert.ExecuteNonQuery();
        manager.LoadPhaseFunctions(compact);
        SetField(manager, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            [nameof(DoodadFuncTimer)] = new() { [1648] = new DoodadFuncTimer { Id = 1648, Delay = 15000, NextPhase = (int)OpenPhase } },
            [nameof(DoodadFuncFinal)] = new() { [1388] = new DoodadFuncFinal { Id = 1388, After = 1000 } },
            [nameof(DoodadFuncRatioChange)] = new()
            {
                [169] = new DoodadFuncRatioChange { Id = 169, Ratio = 5000, NextPhase = (int)OpenPhase },
                [170] = new DoodadFuncRatioChange { Id = 170, Ratio = 5000, NextPhase = (int)InitialPhase }
            }
        });
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [InitialPhase] =
            [
                new()
                {
                    GroupId = InitialPhase, FuncKey = 6385, FuncId = 1264,
                    FuncType = nameof(DoodadFuncFakeUse), NextPhase = (int)OpenPhase
                }
            ]
        });
        SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncFakeUse)] = new()
            {
                [1264] = new DoodadFuncFakeUse { Id = 1264, FakeSkillId = InteractionSkill }
            }
        });
        Install(manager);
        Install(new TaskManager(null));
        var skills = new SkillManager(null, null);
        SetField(skills, "_skills", new Dictionary<uint, SkillTemplate>
        {
            [InteractionSkill] = new() { Id = InteractionSkill }
        });
        Install(skills);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments(5000, true)]
    [Arguments(9999, false)]
    [Arguments(4999, false)]
    public async Task Cocoon_CompletesOnceAfterTimeoutInteractionOrImmediateRatio(int roll, bool interact)
    {
        var doodad = new FixedRollDoodad(roll)
        {
            TemplateId = 3094,
            Template = new DoodadTemplate { Id = 3094 },
            OwnerType = DoodadOwnerType.System
        };
        var player = new CharacterMock { Id = 7, ObjId = 8 };

        var started = DateTime.UtcNow;
        doodad.DoChangePhase(null, (int)InitialPhase);
        if (roll >= 5000)
        {
            await Assert.That(doodad.FuncGroupId).IsEqualTo(InitialPhase);
            var timer = doodad.FuncTask;
            await Assert.That(timer).IsTypeOf<DoodadFuncTimerTask>();
            await Assert.That(timer.TriggerTime >= started.AddSeconds(15) && timer.TriggerTime <= DateTime.UtcNow.AddSeconds(15)).IsTrue();
            if (interact)
                doodad.Use(player, InteractionSkill);
            else
                timer.Execute();
            var finalAfterTransition = doodad.FuncTask;
            timer.Execute(); // A previously dispatched timer cannot repeat the transition.
            await Assert.That(doodad.FuncTask).IsSameReferenceAs(finalAfterTransition);
        }
        await Assert.That(doodad.FuncGroupId).IsEqualTo(OpenPhase);
        await Assert.That(doodad.OverridePhase).IsEqualTo(0);
        await Assert.That(doodad.RollCount).IsEqualTo(2);
        var final = doodad.FuncTask;
        await Assert.That(final).IsTypeOf<DoodadFuncFinalTask>();
        await Assert.That(final.TriggerTime >= started.AddSeconds(1) && final.TriggerTime <= DateTime.UtcNow.AddSeconds(1)).IsTrue();
        final.Execute();
        final.Execute();
        await Assert.That(doodad.DeleteCount).IsEqualTo(1);
        await Assert.That(doodad.FuncTask).IsNull();
    }

    [Test]
    public async Task OtherGroups_KeepTheirFunctionOrder()
    {
        var other = DoodadManager.Instance.GetPhaseFunc(555);
        await Assert.That(other.Select(function => function.FuncId).SequenceEqual([10u, 50u])).IsTrue();
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object instance, string fieldName, object value) => instance.GetType()
        .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class FixedRollDoodad(int roll) : Doodad
    {
        public int RollCount { get; private set; }
        public int DeleteCount { get; private set; }

        public override void Delete() => DeleteCount++;

        protected override int RollPhaseRatio()
        {
            RollCount++;
            return roll;
        }
    }
}
