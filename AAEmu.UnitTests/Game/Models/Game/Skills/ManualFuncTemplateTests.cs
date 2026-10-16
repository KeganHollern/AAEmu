using System.Globalization;

using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed class ManualFuncTemplateTests
{
    [Test]
    public async Task Parse_KeepsTheAuthoredOrderSignsAndRepeatedValues()
    {
        var template = ManualFuncTemplate.Parse(11, "-1000;-1000;-700;-350");

        await Assert.That(template.Id).IsEqualTo(11u);
        await Assert.That(template.Values.SequenceEqual([-1000, -1000, -700, -350])).IsTrue();
    }

    [Test]
    public async Task Parse_UsesInvariantIntegersAndAllowsWhitespace()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var template = ManualFuncTemplate.Parse(15, " 500 ; +150 ; -100 ");
            await Assert.That(template.Values.SequenceEqual([500, 150, -100])).IsTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    [Arguments("")]
    [Arguments(";")]
    [Arguments("100;")]
    [Arguments(";100")]
    [Arguments("100;;200")]
    [Arguments("100;abc;200")]
    [Arguments("100;1.5;200")]
    [Arguments("100;1,000;200")]
    public async Task Parse_InvalidValuesFailWithoutDroppingOrChangingASegment(string values)
    {
        await Assert.That(() => ManualFuncTemplate.Parse(11, values)).Throws<FormatException>();
    }

    [Test]
    [Arguments("2147483648")]
    [Arguments("-2147483649")]
    public async Task Parse_IntegerOverflowFailsWithoutWrapping(string values)
    {
        await Assert.That(() => ManualFuncTemplate.Parse(11, values)).Throws<OverflowException>();
    }

    [Test]
    public async Task ParsedValues_CannotBeChangedAfterRegistration()
    {
        var template = ManualFuncTemplate.Parse(22, "100");
        var values = (IList<int>)template.Values;

        await Assert.That(() => values[0] = 900).Throws<NotSupportedException>();
        await Assert.That(template.Values[0]).IsEqualTo(100);
    }

    [Test]
    [Arguments(int.MinValue, -1000)]
    [Arguments(-1, -1000)]
    [Arguments(0, -1000)]
    [Arguments(1, -1000)]
    [Arguments(2, -700)]
    [Arguments(3, -350)]
    [Arguments(4, -350)]
    [Arguments(int.MaxValue, -350)]
    public async Task GetValue_UsesTheNativeIndexAndKeepsTheFirstAndLastValues(int index, int expected)
    {
        var template = ManualFuncTemplate.Parse(11, "-1000;-1000;-700;-350");

        await Assert.That(template.GetValue(index)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(int.MinValue)]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(int.MaxValue)]
    public async Task GetValue_OneAuthoredValueIsConstantAtEveryIndex(int index)
    {
        var template = ManualFuncTemplate.Parse(22, "100");

        await Assert.That(template.GetValue(index)).IsEqualTo(100);
    }
}
