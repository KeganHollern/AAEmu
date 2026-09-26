using AAEmu.Game.Models.Game.Skills;
using Microsoft.Data.Sqlite;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class UnitPercentBonusTests
{
    [Test]
    [Arguments(50, 50, 200d)]
    [Arguments(50, -50, 100d)]
    [Arguments(-20, -30, 50d)]
    public async Task SimultaneousPercentBonuses_AddBeforeOneMultiplication(int first, int second, double expected)
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.Str, first, UnitModifierType.Percent));
        unit.AddBonus(2, Modifier(UnitAttribute.Str, second, UnitModifierType.Percent));
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(expected);
        unit.RemoveBonus(1, UnitAttribute.Str);
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(100d + second);
    }

    [Test]
    public async Task FlatAndPercentBonuses_DoNotDependOnInsertionOrder()
    {
        var first = new Unit();
        var second = new Unit();
        var modifiers = new[] { Modifier(UnitAttribute.Str, 50, UnitModifierType.Percent),
            Modifier(UnitAttribute.Str, 20), Modifier(UnitAttribute.Str, 50, UnitModifierType.Percent) };
        for (uint index = 0; index < modifiers.Length; index++)
        {
            first.AddBonus(index, modifiers[index]);
            second.AddBonus(index, modifiers[modifiers.Length - 1 - index]);
        }
        await Assert.That(first.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(240d);
        await Assert.That(second.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(240d);
    }

    [Test]
    public async Task DynamicPercent_CombinesWithStaticPercentAtReadTimeAndStopsAfterRemoval()
    {
        var unit = new Unit();
        var buff = new Buff(unit, unit, new SkillCasterUnit(1), new BuffTemplate { Id = 10 }, null, DateTime.UtcNow)
            { Duration = 1000, StartTime = DateTime.UtcNow.AddSeconds(10) };
        unit.AddBonus(1, Modifier(UnitAttribute.Str, 50, UnitModifierType.Percent));
        unit.AddDynamicBonus(2, new DynamicBonus
        {
            Template = new DynamicBonusTemplate { Attribute = UnitAttribute.Str, ModifierType = UnitModifierType.Percent, FuncType = "LinearFunc" },
            SourceBuff = buff, LinearFunc = new LinearFuncTemplate { StartValue = 25, EndValue = 50 }
        });
        unit.AddDynamicBonus(3, new DynamicBonus
        {
            Template = new DynamicBonusTemplate { Attribute = UnitAttribute.Str, ModifierType = UnitModifierType.Value, FuncType = "LinearFunc" },
            SourceBuff = buff, LinearFunc = new LinearFuncTemplate { StartValue = 20, EndValue = 40 }
        });
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(210d);
        buff.StartTime = DateTime.UtcNow.AddSeconds(-10);
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(280d);
        unit.RemoveDynamicBonus(2, UnitAttribute.Str);
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(210d);
        unit.RemoveDynamicBonus(3, UnitAttribute.Str);
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(150d);
    }

    [Test]
    public async Task UnsupportedDynamicFunction_DoesNotAlterThePercentSum()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.Str, 50, UnitModifierType.Percent));
        unit.AddDynamicBonus(2, new DynamicBonus
        {
            Template = new DynamicBonusTemplate { Attribute = UnitAttribute.Str, ModifierType = UnitModifierType.Percent, FuncType = "ManualFunc" },
            SourceBuff = new Buff(unit, unit, new SkillCasterUnit(1), new BuffTemplate { Id = 10 }, null, DateTime.UtcNow)
        });
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(150d);
    }

    [Test]
    public async Task ExactClient_DistinctHealthBuffsShareOnePercentAccumulator()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // These different buff IDs have no shared group and each uses Refresh.
        command.CommandText = """
            SELECT m.owner_id, m.value FROM unit_modifiers m JOIN buffs b ON b.id=m.owner_id
            WHERE m.owner_type='Buff' AND m.unit_attribute_id=6 AND m.unit_modifier_type_id=1
              AND m.owner_id IN (926,1384) AND b.group_id IS NULL AND b.stack_rule_id=1
            """;
        using var reader = command.ExecuteReader();
        var unit = new Unit();
        var count = 0;
        while (reader.Read())
        {
            unit.AddBonus((uint)reader.GetInt32(0), Modifier(UnitAttribute.MaxHealth, reader.GetInt32(1), UnitModifierType.Percent));
            count++;
        }
        await Assert.That(count).IsEqualTo(2);
        await Assert.That(unit.CalculateWithBonuses(1000, UnitAttribute.MaxHealth)).IsEqualTo(1500d);
    }

    private static Bonus Modifier(UnitAttribute attribute, int value, UnitModifierType type = UnitModifierType.Value) => new()
    {
        Template = new BonusTemplate { Attribute = attribute, ModifierType = type }, Value = value
    };
}
