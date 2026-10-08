using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

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

        // r208022 cocoon 3094 has two ordered 50% choices. Row 170 selects
        // the current phase, then interaction 6385 must still open phase 7080.
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [InitialPhase] =
            [
                new() { GroupId = InitialPhase, FuncId = 169, FuncType = nameof(DoodadFuncRatioChange) },
                new() { GroupId = InitialPhase, FuncId = 170, FuncType = nameof(DoodadFuncRatioChange) }
            ]
        });
        SetField(manager, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
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
    [Arguments(5000)]
    [Arguments(9999)]
    public async Task SelfSelection_NextNormalUseFollowsAuthoredInteraction(int roll)
    {
        var doodad = new FixedRollDoodad(roll)
        {
            TemplateId = 3094,
            Template = new DoodadTemplate { Id = 3094 },
            OwnerType = DoodadOwnerType.System
        };
        var player = new CharacterMock { Id = 7, ObjId = 8 };

        var stopped = doodad.DoChangePhase(null, (int)InitialPhase);
        var settledPhase = doodad.FuncGroupId;
        doodad.Use(player, InteractionSkill);

        await Assert.That(stopped).IsTrue();
        await Assert.That(settledPhase).IsEqualTo(InitialPhase);
        await Assert.That(doodad.FuncGroupId).IsEqualTo(OpenPhase);
        await Assert.That(doodad.OverridePhase).IsEqualTo(0);
        await Assert.That(doodad.RollCount).IsEqualTo(1);
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

        protected override int RollPhaseRatio()
        {
            RollCount++;
            return roll;
        }
    }
}
