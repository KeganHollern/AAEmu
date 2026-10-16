using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class UnitVisibilityTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private Unit _observer;
    private Unit _target;

    [Before(Test)]
    public void SetUp()
    {
        ReplaceSingleton(new DuelManager());
        ReplaceSingleton(new UnitAttributeLimitsGameData());
        var skills = new SkillManager(null, null);
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>
        {
            [(uint)TagsEnum.Stealth] = [100]
        });
        ReplaceSingleton(skills);
        var formulas = new FormulaManager();
        ReplaceSingleton(formulas);
        typeof(FormulaManager).GetProperty("CalculationEngine")!.SetValue(formulas, new Jace.CalculationEngine());
        SetField(formulas, "_formulas", new Dictionary<uint, Formula>
        {
            [4] = new("min(40, 12 * (1 + (source_level - target_level) / 50) / (1 + stealth_level / 50))")
                { Id = 4 },
            [10] = new("0.5") { Id = 10 }
        });
        _observer = new Unit { ObjId = 1, Level = 50, IsVisible = true };
        _target = new Unit { ObjId = 2, Level = 50, IsVisible = true };
        _target.Transform.Local.SetPosition(0, 5, 0);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _singletons.Clear();
    }

    [Test]
    public async Task NoStealthFlag_RemainsVisibleAtLongRange()
    {
        _target.Transform.Local.SetPosition(0, 100, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsTrue();
    }

    [Test]
    public async Task NullTarget_IsNotVisible()
    {
        await Assert.That(_observer.CanSeeTarget(null)).IsFalse();
    }

    [Test]
    public async Task HiddenTarget_RemainsHiddenWithAntiStealth()
    {
        AddBuff(_target, new BuffTemplate { AntiStealth = true });
        _target.IsVisible = false;

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();
    }

    [Test]
    public async Task StealthedObserver_CanSeeItself()
    {
        AddStealth(_observer, 50);

        await Assert.That(_observer.CanSeeTarget(_observer)).IsTrue();
    }

    [Test]
    [Arguments(5.99f, true)]
    [Arguments(6f, false)]
    [Arguments(6.01f, false)]
    public async Task FrontDetection_UsesFormulaAndStrictBoundary(float distance, bool expected)
    {
        AddStealth(_target, 50);
        _target.Transform.Local.SetPosition(0, distance, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0.49f, true)]
    [Arguments(0.5f, false)]
    [Arguments(0.51f, false)]
    public async Task RearDetection_UsesFormulaAndStrictBoundary(float distance, bool expected)
    {
        AddStealth(_target, 50);
        _target.Transform.Local.SetPosition(0, -distance, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsEqualTo(expected);
    }

    [Test]
    public async Task TargetBesideObserver_UsesFrontFormula()
    {
        AddStealth(_target, 50);
        _target.Transform.Local.SetPosition(5, 0, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsTrue();
    }

    [Test]
    public async Task MultipleStealthBuffs_UseHighestActiveAbilityLevel()
    {
        AddStealth(_target, 1);
        var stronger = AddStealth(_target, 50);
        _target.Transform.Local.SetPosition(0, 8, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();

        stronger.InUse = false;

        await Assert.That(_observer.CanSeeTarget(_target)).IsTrue();
    }

    [Test]
    [Arguments(EffectState.Created, false)]
    [Arguments(EffectState.Finishing, true)]
    [Arguments(EffectState.Finished, true)]
    public async Task InactiveStealth_DoesNotHideTarget(EffectState state, bool inUse)
    {
        var stealth = AddStealth(_target, 50);
        stealth.State = state;
        stealth.InUse = inUse;
        _target.Transform.Local.SetPosition(0, 100, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsTrue();
    }

    [Test]
    public async Task TargetAntiStealth_BypassesStealthDetectionRangeUntilItEnds()
    {
        AddStealth(_target, 50);
        var reveal = AddBuff(_target, new BuffTemplate { AntiStealth = true });
        _target.Transform.Local.SetPosition(0, 100, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsTrue();

        reveal.State = EffectState.Finished;

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();
    }

    [Test]
    public async Task ObserverAntiStealth_DoesNotRevealAnotherTarget()
    {
        AddStealth(_target, 50);
        AddBuff(_observer, new BuffTemplate { AntiStealth = true });
        _target.Transform.Local.SetPosition(0, 100, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DetectStealthFlag_DoesNotBecomeAnUnconfirmedRangeBypass(bool onObserver)
    {
        AddStealth(_target, 50);
        AddBuff(onObserver ? _observer : _target, new BuffTemplate { DetectStealth = true });
        _target.Transform.Local.SetPosition(0, 100, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();
    }

    [Test]
    public async Task StealthTagWithoutFlag_DoesNotSubstituteForNativeStealthState()
    {
        AddBuff(_target, new BuffTemplate { Id = 100 });
        _target.Transform.Local.SetPosition(0, 100, 0);

        await Assert.That(_observer.CanSeeTarget(_target)).IsTrue();
    }

    [Test]
    public async Task DetectionRangeBonus_UsesPercentAndEndsAfterRemoval()
    {
        AddStealth(_target, 50);
        _target.Transform.Local.SetPosition(0, 8, 0);
        _observer.AddBonus(10, new Bonus
        {
            Template = new BonusTemplate
            {
                Attribute = UnitAttribute.DetectStealthRangeMul, ModifierType = UnitModifierType.Value
            },
            Value = 50
        });

        await Assert.That(_observer.CanSeeTarget(_target)).IsTrue();

        _observer.RemoveBonus(10, UnitAttribute.DetectStealthRangeMul);

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();
    }

    [Test]
    public async Task TargetRangeBonus_DoesNotChangeObserverDetection()
    {
        AddStealth(_target, 50);
        _target.Transform.Local.SetPosition(0, 8, 0);
        _target.AddBonus(10, new Bonus
        {
            Template = new BonusTemplate
            {
                Attribute = UnitAttribute.DetectStealthRangeMul, ModifierType = UnitModifierType.Value
            },
            Value = 100
        });

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();
    }

    [Test]
    public async Task HeightDifference_ContributesToDetectionDistance()
    {
        AddStealth(_target, 50);
        _target.Transform.Local.SetPosition(0, 5, 4);

        await Assert.That(_observer.CanSeeTarget(_target)).IsFalse();
    }

    [Test]
    [Arguments(1f, true)]
    [Arguments(-1f, false)]
    public async Task FullObserverRotation_SelectsFrontAndRearInThreeDimensions(float direction, bool expected)
    {
        AddStealth(_target, 50);
        _observer.Transform.Local.Rotation = new Vector3(MathF.PI / 2, 0, 0);
        _target.Transform.Local.SetPosition(0, 0, direction);

        await Assert.That(_observer.CanSeeTarget(_target)).IsEqualTo(expected);
    }

    private static Buff AddStealth(Unit unit, uint level)
    {
        var buff = AddBuff(unit, new BuffTemplate { Stealth = true });
        buff.AbLevel = level;
        return buff;
    }

    private static Buff AddBuff(Unit unit, BuffTemplate template)
    {
        var buff = new Buff(unit, unit, new SkillCasterUnit(unit.ObjId), template, null, DateTime.UtcNow)
        {
            InUse = true, State = EffectState.Acting
        };
        var effects = (List<Buff>)typeof(Buffs).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(unit.Buffs)!;
        effects.Add(buff);
        return buff;
    }

    private void ReplaceSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private static void SetField<T>(T value, string field, object setting) =>
        typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, setting);
}
