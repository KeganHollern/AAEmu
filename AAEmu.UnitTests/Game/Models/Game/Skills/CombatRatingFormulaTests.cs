using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Formulas;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class CombatRatingFormulaTests
{
    private static readonly FieldInfo FormulaInstance = typeof(Singleton<FormulaManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previous;
    private Dictionary<uint, Formula> _formulas;

    [Before(Test)]
    public void SetUp()
    {
        _previous = FormulaInstance.GetValue(null);
        var manager = new FormulaManager();
        FormulaInstance.SetValue(null, manager);
        typeof(FormulaManager).GetProperty("CalculationEngine")!.SetValue(manager, new Jace.CalculationEngine());
        // Exact r208022 formulas 24, 25, and 26 from the pinned server compact.
        _formulas = new()
        {
            [24] = new("bulls_eye * 105"),
            [25] = new("flexibility * 90"),
            [26] = new("flexibility / 10")
        };
        typeof(FormulaManager).GetField("_formulas", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, _formulas);
    }

    [After(Test)]
    public void TearDown() => FormulaInstance.SetValue(null, _previous);

    [Test]
    [Arguments(1000, 2100000, 5f)]
    [Arguments(1000, 4200000, 2.5f)]
    [Arguments(999, 3286817, 3.1913855f)]
    public async Task BullsEye_UsesTheAuthoredWeightAndOwnerFacets(int rating, int facets, float expected)
    {
        await Assert.That(CombatFormulas.GetBullsEyeReduction(rating, facets)).IsEqualTo(expected).Within(0.00001f);
    }

    [Test]
    [Arguments(1000, 1800000, 5f)]
    [Arguments(1000, 3600000, 2.5f)]
    [Arguments(999, 3286817, 2.7354732f)]
    public async Task CriticalChance_UsesTheAuthoredWeightWithoutIntegerTruncation(int rating, int facets, float expected)
    {
        await Assert.That(CombatFormulas.GetCriticalChanceReduction(rating, facets)).IsEqualTo(expected).Within(0.00001f);
    }

    [Test]
    [Arguments(99, 0.99f)]
    [Arguments(101, 1.01f)]
    [Arguments(999, 9.99f)]
    [Arguments(1000, 10f)]
    public async Task CriticalBonus_ConvertsAuthoredTenthsToPercentagePoints(int rating, float expected)
    {
        await Assert.That(CombatFormulas.GetCriticalBonusReduction(rating)).IsEqualTo(expected).Within(0.00001f);
    }

    [Test]
    [Arguments(0, 100)]
    [Arguments(-1, 100)]
    [Arguments(100, 0)]
    [Arguments(100, -1)]
    public async Task InvalidRatingOrFacets_DoNotProduceAnInvalidProbability(int rating, int facets)
    {
        await Assert.That(CombatFormulas.GetBullsEyeReduction(rating, facets)).IsEqualTo(0f);
        await Assert.That(CombatFormulas.GetCriticalChanceReduction(rating, facets)).IsEqualTo(0f);
    }

    [Test]
    public async Task LargeRatings_CannotReduceProbabilityBeyondOneHundredPoints()
    {
        await Assert.That(CombatFormulas.GetBullsEyeReduction(int.MaxValue, 1)).IsEqualTo(100f);
        await Assert.That(CombatFormulas.GetCriticalChanceReduction(int.MaxValue, 1)).IsEqualTo(100f);
    }

    [Test]
    public async Task RatingConversions_ReadTheLoadedFormulas()
    {
        _formulas[24] = new Formula("bulls_eye * 210");
        _formulas[25] = new Formula("flexibility * 180");
        _formulas[26] = new Formula("flexibility / 5");

        await Assert.That(CombatFormulas.GetBullsEyeReduction(1000, 2100000)).IsEqualTo(10f);
        await Assert.That(CombatFormulas.GetCriticalChanceReduction(1000, 1800000)).IsEqualTo(10f);
        await Assert.That(CombatFormulas.GetCriticalBonusReduction(1000)).IsEqualTo(20f);
    }
}
