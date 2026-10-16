using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class ManualDynamicBonusTests
{
    private static readonly DateTime s_now = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private readonly Dictionary<uint, ManualFuncTemplate> _functions = [];

    [Before(Test)]
    public void SetUp()
    {
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_manualFuncs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, _functions);
        Replace(skills);
        Replace(new DuelManager());
        Replace(new MateGameData());
        Replace(new UnitAttributeLimitsGameData());
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    public async Task Evaluate_MissingFunctionDoesNotApplyAZeroOrAnotherFunction()
    {
        var unit = new Unit();
        var bonus = new DynamicBonus
        {
            Template = Modifier(22),
            SourceBuff = CreateBuff(unit, new BuffTemplate { Id = 739, Duration = 2000, Tick = 1000 }),
            LinearFunc = new LinearFuncTemplate { StartValue = 900, EndValue = 900 }
        };

        await Assert.That(bonus.Evaluate(s_now, out var value)).IsFalse();
        await Assert.That(value).IsEqualTo(0d);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Evaluate_MissingSourceOrModifierDoesNotApplyTheFunction(bool missingSource)
    {
        var unit = new Unit();
        var bonus = new DynamicBonus
        {
            Template = missingSource ? Modifier(22) : null,
            SourceBuff = missingSource ? null : CreateBuff(unit, new BuffTemplate { Id = 739, Duration = 2000, Tick = 1000 }),
            ManualFunc = ManualFuncTemplate.Parse(22, "100")
        };

        await Assert.That(bonus.Evaluate(s_now, out var value)).IsFalse();
        await Assert.That(value).IsEqualTo(0d);
    }

    [Test]
    [Arguments(-1, 100)]
    [Arguments(0, 100)]
    [Arguments(9999999, 100)]
    [Arguments(10000000, 200)]
    [Arguments(19999999, 200)]
    [Arguments(20000000, 300)]
    [Arguments(29999999, 300)]
    [Arguments(30000000, 400)]
    [Arguments(99000000, 400)]
    public async Task Evaluate_ChangesAtEachRuntimeTickBoundary(int elapsedTicks, int expected)
    {
        var unit = new Unit();
        var buff = CreateBuff(unit, new BuffTemplate { Duration = 10000, Tick = 1000 });
        var bonus = new DynamicBonus
        {
            Template = Modifier(1), SourceBuff = buff,
            ManualFunc = ManualFuncTemplate.Parse(1, "100;200;300;400")
        };

        await Assert.That(bonus.Evaluate(s_now.AddTicks(elapsedTicks), out var value)).IsTrue();
        await Assert.That(value).IsEqualTo((double)expected);
    }

    [Test]
    [Arguments(0, 1000d)]
    [Arguments(-1, 1000d)]
    [Arguments(5000, 0d)]
    [Arguments(5000, -1000d)]
    public async Task Evaluate_NonpositiveRuntimeDurationOrTickDoesNotApplyTheFunction(int duration, double tick)
    {
        var unit = new Unit();
        var buff = CreateBuff(unit, new BuffTemplate { Duration = 5000, Tick = 1000 });
        buff.Duration = duration;
        buff.Tick = tick;
        var bonus = new DynamicBonus
        {
            Template = Modifier(1), SourceBuff = buff, ManualFunc = ManualFuncTemplate.Parse(1, "500")
        };

        await Assert.That(bonus.Evaluate(s_now, out var value)).IsFalse();
        await Assert.That(value).IsEqualTo(0d);
    }

    [Test]
    public async Task Evaluate_UsesRuntimeTickWithoutScalingTheListToDurationOrCharge()
    {
        var unit = new Unit();
        var buff = CreateBuff(unit, new BuffTemplate { Duration = 30000, Tick = 25000 });
        buff.Duration = 5000;
        buff.Tick = 1000;
        var bonus = new DynamicBonus
        {
            Template = Modifier(11), SourceBuff = buff,
            ManualFunc = ManualFuncTemplate.Parse(11, "-1000;-1000;-700;-350")
        };

        await Assert.That(bonus.Evaluate(s_now.AddMilliseconds(2000), out var value)).IsTrue();
        await Assert.That(value).IsEqualTo(-700d);
        buff.Duration = 10000;
        buff.Charge = 8;
        await Assert.That(bonus.Evaluate(s_now.AddMilliseconds(2000), out value)).IsTrue();
        await Assert.That(value).IsEqualTo(-700d);
    }

    [Test]
    public async Task Start_MissingFunctionSkipsTheModifierAndKeepsOtherBonuses()
    {
        var unit = new Unit();
        var template = new BuffTemplate { Id = 739, Duration = 2000, Tick = 1000 };
        template.DynamicBonuses.Add(Modifier(22));
        template.Bonuses.Add(new BonusTemplate
        {
            Attribute = UnitAttribute.MoveSpeedMul, ModifierType = UnitModifierType.Value, Value = 250
        });
        var buff = CreateBuff(unit, template);

        template.Start(unit, unit, buff);

        await Assert.That(unit.GetDynamicBonuses(UnitAttribute.MoveSpeedMul).Count).IsEqualTo(0);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.25f);
        template.Dispel(unit, unit, buff);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1f);
    }

    [Test]
    public async Task Start_RepeatedRegistrationDoesNotDuplicateTheManualContribution()
    {
        var unit = new Unit();
        _functions[22] = ManualFuncTemplate.Parse(22, "100");
        var template = new BuffTemplate { Id = 739, Duration = 2000, Tick = 1000 };
        template.DynamicBonuses.Add(Modifier(22));
        var buff = CreateBuff(unit, template);
        buff.StartTime = DateTime.UtcNow;

        template.Start(unit, unit, buff);
        template.Start(unit, unit, buff);

        await Assert.That(unit.GetDynamicBonuses(UnitAttribute.MoveSpeedMul).Count).IsEqualTo(1);
        await Assert.That(unit.GetDynamicBonuses(UnitAttribute.MoveSpeedMul)[0].SourceBuff).IsSameReferenceAs(buff);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.1f);
        template.Dispel(unit, unit, buff);
        await Assert.That(unit.GetDynamicBonuses(UnitAttribute.MoveSpeedMul).Count).IsEqualTo(0);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1f);
    }

    [Test]
    [Arguments(739u, 22u, "100", 2000, 0)]
    [Arguments(930u, 11u, "-1000;-1000;-700;-350", 5000, 1000)]
    [Arguments(1639u, 15u, "500;150", 30000, 25000)]
    public async Task ExactClient_ManualFunctionsKeepTheirAuthoredBuffAndTiming(
        uint buffId, uint functionId, string values, int duration, int tick)
    {
        using var connection = OpenCompact();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.duration, b.tick, b.level_duration, d.func_id,
                   d.unit_attribute_id, d.unit_modifier_type_id, f.value_list
            FROM dynamic_unit_modifiers d
            JOIN buffs b ON b.id = d.buff_id
            JOIN manual_funcs f ON f.id = d.func_id
            WHERE d.func_type = 'ManualFunc' AND d.buff_id = $buff
            """;
        command.Parameters.AddWithValue("$buff", buffId);
        using var reader = command.ExecuteReader();

        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt32(0)).IsEqualTo(duration);
        await Assert.That(reader.GetInt32(1)).IsEqualTo(tick);
        await Assert.That(reader.GetInt32(2)).IsEqualTo(0);
        await Assert.That((uint)reader.GetInt32(3)).IsEqualTo(functionId);
        await Assert.That(reader.GetInt32(4)).IsEqualTo((int)UnitAttribute.MoveSpeedMul);
        await Assert.That(reader.GetInt32(5)).IsEqualTo((int)UnitModifierType.Value);
        await Assert.That(reader.GetString(6)).IsEqualTo(values);
        await Assert.That(reader.Read()).IsFalse();
    }

    [Test]
    [Arguments(739u, 1000, 1f)]
    [Arguments(930u, 500, 0f)]
    [Arguments(930u, 1500, 0f)]
    [Arguments(930u, 2500, 0.3f)]
    [Arguments(930u, 3500, 0.65f)]
    [Arguments(1639u, 10000, 1.5f)]
    [Arguments(1639u, 27000, 1.15f)]
    public async Task ExactClient_ManualBuffsAffectMovementAtTheirAuthoredTicks(
        uint buffId, int elapsedMilliseconds, float expected)
    {
        var (template, function) = LoadAuthored(buffId);
        _functions[function.Id] = function;
        var unit = new Unit();
        var buff = CreateBuff(unit, template);
        template.Start(unit, unit, buff);

        // The property uses the live clock. Set the source time inside a sample interval.
        buff.StartTime = DateTime.UtcNow.AddMilliseconds(-elapsedMilliseconds);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(expected);
        template.Dispel(unit, unit, buff);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1f);
    }

    [Test]
    public async Task Refresh_RestartsTheManualSequenceAndDispelKeepsUnrelatedBonuses()
    {
        var (template, function) = LoadAuthored(1639);
        _functions[function.Id] = function;
        var unit = new Unit();
        unit.AddBonus(1, new Bonus
        {
            Template = new BonusTemplate
            {
                Attribute = UnitAttribute.MoveSpeedMul, ModifierType = UnitModifierType.Value
            },
            Value = 200
        });
        var buff = CreateBuff(unit, template);
        template.Start(unit, unit, buff);
        buff.StartTime = DateTime.UtcNow.AddSeconds(-27);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.35f);

        var replacement = CreateBuff(unit, template);
        buff.ApplyRefreshState(replacement, DateTime.UtcNow);
        template.Start(unit, unit, buff);

        await Assert.That(unit.GetDynamicBonuses(UnitAttribute.MoveSpeedMul).Count).IsEqualTo(1);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.7f);
        template.Dispel(unit, unit, buff);
        await Assert.That(unit.GetDynamicBonuses(UnitAttribute.MoveSpeedMul).Count).IsEqualTo(0);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.2f);
    }

    private static DynamicBonusTemplate Modifier(uint functionId) => new()
    {
        Attribute = UnitAttribute.MoveSpeedMul, ModifierType = UnitModifierType.Value,
        FuncId = functionId, FuncType = "ManualFunc"
    };

    private static Buff CreateBuff(Unit unit, BuffTemplate template) =>
        new(unit, unit, new SkillCasterUnit(1), template, null, s_now)
        {
            Index = 2, Duration = template.Duration, Tick = template.Tick, Passive = true
        };

    private static SqliteConnection OpenCompact()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = compact, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        return connection;
    }

    private static (BuffTemplate Template, ManualFuncTemplate Function) LoadAuthored(uint buffId)
    {
        using var connection = OpenCompact();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.duration, b.tick, b.stack_rule_id, d.func_id, f.value_list
            FROM dynamic_unit_modifiers d
            JOIN buffs b ON b.id = d.buff_id
            JOIN manual_funcs f ON f.id = d.func_id
            WHERE d.func_type = 'ManualFunc' AND d.buff_id = $buff
            """;
        command.Parameters.AddWithValue("$buff", buffId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException($"Missing authored manual modifier for buff {buffId}.");
        var template = new BuffTemplate
        {
            Id = buffId, Duration = reader.GetInt32(0), Tick = reader.GetInt32(1),
            StackRule = (BuffStackRule)reader.GetInt32(2)
        };
        var function = ManualFuncTemplate.Parse((uint)reader.GetInt32(3), reader.GetString(4));
        template.DynamicBonuses.Add(Modifier(function.Id));
        return (template, function);
    }

    private void Replace<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }
}
