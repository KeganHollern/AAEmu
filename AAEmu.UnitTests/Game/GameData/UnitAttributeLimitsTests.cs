using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

[NotInParallel]
public sealed class UnitAttributeLimitsTests
{
    private static readonly FieldInfo s_instance = typeof(Singleton<UnitAttributeLimitsGameData>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_formulaInstance = typeof(Singleton<FormulaManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previousFormula;
    private object _previous;
    private UnitAttributeLimitsGameData _data;

    [Before(Test)]
    public void SetUp()
    {
        _previous = s_instance.GetValue(null);
        _previousFormula = s_formulaInstance.GetValue(null);
        _data = new UnitAttributeLimitsGameData();
        using var connection = CreateData("(10,-10000,3000),(71,-600,4000),(51,-1000,5000),(120,-800,5000),(8,0,1000000)");
        _data.Load(connection);
        s_instance.SetValue(null, _data);
    }

    [After(Test)]
    public void TearDown()
    {
        s_instance.SetValue(null, _previous);
        s_formulaInstance.SetValue(null, _previousFormula);
    }

    [Test]
    [Arguments(0, 1f)]
    [Arguments(300, 1.3f)]
    [Arguments(3000, 4f)]
    [Arguments(9000, 4f)]
    [Arguments(-1000, 0f)]
    [Arguments(-20000, -9f)]
    public async Task Movement_ClampsRawBonusBeforeRestoringTheNormalSpeedBaseline(int bonus, float expected)
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MoveSpeedMul, bonus));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(expected);
    }

    [Test]
    public async Task Movement_StackedBonusesReachTheAuthoredCapAndRemovalRestoresTheValue()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MoveSpeedMul, 2000));
        unit.AddBonus(2, Modifier(UnitAttribute.MoveSpeedMul, 2000));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(4f);
        unit.RemoveBonus(2, UnitAttribute.MoveSpeedMul);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(3f);
    }

    [Test]
    public async Task Movement_PercentBonusPreservesTheNormalSpeedBaseline()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MoveSpeedMul, 50, UnitModifierType.Percent));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.5f);
        unit.AddBonus(2, Modifier(UnitAttribute.MoveSpeedMul, 1000));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(3f);
    }

    [Test]
    public async Task FinalClamp_AllowsAPercentReductionAfterLargeFlatBonuses()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MoveSpeedMul, 6000));
        unit.AddBonus(2, Modifier(UnitAttribute.MoveSpeedMul, -75, UnitModifierType.Percent));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.75f);
    }

    [Test]
    public async Task FinalClamp_IncludesDynamicFlatAndPercentBonuses()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MoveSpeedMul, 1000));
        unit.AddDynamicBonus(2, DynamicModifier(unit, 5000, UnitModifierType.Value));
        unit.AddDynamicBonus(3, DynamicModifier(unit, -75, UnitModifierType.Percent));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(1.75f);
        unit.RemoveDynamicBonus(3, UnitAttribute.MoveSpeedMul);
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(4f);
    }

    [Test]
    public async Task CharacterModifiers_ClampBeforeDurationDamageAndHealingConversions()
    {
        var character = new CharacterMock();
        character.AddBonus(1, Modifier(UnitAttribute.CastingTimeMul, -5000));
        character.AddBonus(2, Modifier(UnitAttribute.MeleeDamageMul, 10000));
        character.AddBonus(3, Modifier(UnitAttribute.HealMul, 10000));
        await Assert.That(character.CastTimeMul).IsEqualTo(0.4f);
        await Assert.That(character.MeleeDamageMul).IsEqualTo(6f);
        await Assert.That(character.HealMul).IsEqualTo(6f);
    }

    [Test]
    public async Task UnlistedAttribute_DoesNotAcquireAnInventedPrimaryStatLimit()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.Str, 20000));
        await Assert.That(unit.CalculateWithBonuses(100, UnitAttribute.Str)).IsEqualTo(20100d);
    }

    [Test]
    public async Task Loader_LeavesXpAndGcdWithTheirCurrentLoaders()
    {
        using var connection = CreateData("(95,0,1),(186,0,1),(74,-100,100)");
        _data.Load(connection);
        await Assert.That(_data.Clamp(UnitAttribute.ExpMul, 500)).IsEqualTo(500d);
        await Assert.That(_data.Clamp(UnitAttribute.ExpByLaborPowerMul, 500)).IsEqualTo(500d);
        await Assert.That(_data.Clamp(UnitAttribute.GlobalCooldownMul, 500)).IsEqualTo(500d);
    }

    [Test]
    [Arguments("(10,3000,-10000)")]
    [Arguments("(10,0,10),(10,0,20)")]
    [Arguments("(256,0,100)")]
    [Arguments("(-1,0,100)")]
    public async Task Loader_InvalidRowsLeaveTheLastCompleteSnapshot(string rows)
    {
        using var connection = CreateData(rows);
        await Assert.That(() => _data.Load(connection)).Throws<InvalidDataException>();
        await Assert.That(_data.Clamp(UnitAttribute.MoveSpeedMul, 5000)).IsEqualTo(3000d);
    }

    [Test]
    public async Task Loader_ReloadReplacesRemovedLimits()
    {
        using var connection = CreateData("(8,0,200)");
        _data.Load(connection);
        await Assert.That(_data.Clamp(UnitAttribute.MoveSpeedMul, 5000)).IsEqualTo(5000d);
        await Assert.That(_data.Clamp(UnitAttribute.Armor, 5000)).IsEqualTo(200d);
    }

    [Test]
    public async Task RawAttributeBounds_IncludeBothEndpointsAndApplyAfterBaseAndBonuses()
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.Armor, 200));
        await Assert.That(unit.CalculateWithBonuses(999800, UnitAttribute.Armor)).IsEqualTo(1000000d);
        await Assert.That(unit.CalculateWithBonuses(2000000, UnitAttribute.Armor)).IsEqualTo(1000000d);
        await Assert.That(unit.CalculateWithBonuses(-200, UnitAttribute.Armor)).IsEqualTo(0d);
        await Assert.That(unit.CalculateWithBonuses(-1000, UnitAttribute.Armor)).IsEqualTo(0d);
    }

    [Test]
    [Arguments(-50, 0.5f)]
    [Arguments(-80, 0.2f)]
    [Arguments(-100, 0f)]
    public async Task Movement_StandaloneAuthoredPercentSlowsStillApply(int percent, float expected)
    {
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MoveSpeedMul, percent, UnitModifierType.Percent));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(expected);
    }

    [Test]
    [Arguments(300, -250, 150f)]
    [Arguments(-300, 250, 50f)]
    [Arguments(50, 200, 200f)]
    public async Task SpellCritical_ClampsAfterBothAttributeContributions(int primary, int damage, float expected)
    {
        using var connection = CreateData("(30,0,200)");
        _data.Load(connection);
        InstallFormulas(kind => kind is UnitFormulaKind.Facet or UnitFormulaKind.SpellCritical ? 100 : 0);
        var character = new CharacterMock();
        character.Equipment = new ItemContainer(1, SlotType.Equipment, false, character);
        character.AddBonus(1, Modifier(UnitAttribute.SpellCritical, primary));
        character.AddBonus(2, Modifier(UnitAttribute.SpellDamageCritical, damage));
        await Assert.That(character.SpellCritical).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1000d)]
    [Arguments(3000000000d)]
    public async Task ManualHealthGetters_ClampBeforeAnyIntegerOverflow(double formulaValue)
    {
        using var connection = CreateData("(6,-2000000000,10000000)");
        _data.Load(connection);
        InstallFormulas(kind => kind == UnitFormulaKind.MaxHealth ? formulaValue : 0);
        Unit[] units = [new Npc { Template = new NpcTemplate() }, new Mate { Template = new NpcTemplate() },
            new Slave(), new Transfer(), new Shipyard()];
        foreach (var unit in units)
        {
            unit.AddBonus(1, Modifier(UnitAttribute.MaxHealth, int.MaxValue));
            unit.AddBonus(2, Modifier(UnitAttribute.MaxHealth, 200, UnitModifierType.Percent));
            await Assert.That(unit.MaxHp).IsEqualTo(10000000);
        }
    }

    [Test]
    public async Task ManualHealthGetters_PreservePerContributionTruncation()
    {
        using var connection = CreateData("(6,-2000000000,10000000)");
        _data.Load(connection);
        InstallFormulas(kind => kind == UnitFormulaKind.MaxHealth ? 101.9 : 0);
        Unit[] units = [new Npc { Template = new NpcTemplate() }, new Slave(), new Transfer(), new Shipyard()];
        foreach (var unit in units)
        {
            unit.AddBonus(1, Modifier(UnitAttribute.MaxHealth, -50, UnitModifierType.Percent));
            unit.AddBonus(2, Modifier(UnitAttribute.MaxHealth, 50, UnitModifierType.Percent));
            await Assert.That(unit.MaxHp).IsEqualTo(76);
        }
    }

    [Test]
    public async Task ManualArmorAndDpsGetters_UseTheSameFinalRawLimits()
    {
        using var connection = CreateData("(8,0,1000000),(33,0,10000000),(34,0,10000000),(35,0,10000000)");
        _data.Load(connection);
        InstallFormulas(kind => kind is UnitFormulaKind.Armor or UnitFormulaKind.MeleeDpsInc or
            UnitFormulaKind.RangedDpsInc or UnitFormulaKind.SpellDpsInc ? 3000000000d : 0);
        Unit[] units = [new Npc { Template = new NpcTemplate() }, new Mate { Template = new NpcTemplate() }, new Slave()];
        foreach (var unit in units)
        {
            unit.AddBonus(1, Modifier(UnitAttribute.Armor, 200, UnitModifierType.Percent));
            await Assert.That(unit.Armor).IsEqualTo(1000000);
            await Assert.That(unit.DpsInc).IsEqualTo(10000000);
            await Assert.That(unit.MDpsInc).IsEqualTo(10000000);
            // Mate does not define a ranged-DPS getter.
            if (unit is not Mate)
                await Assert.That(unit.RangedDpsInc).IsEqualTo(10000000);
        }
    }

    [Test]
    public async Task ExactClient_LoadsAllUncoveredLimitsWithoutChangingTheirRawUnits()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        _data.Load(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT unit_attribute_id, minimum, maximum FROM unit_attribute_limits WHERE unit_attribute_id NOT IN (74,95,186)";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var attribute = (UnitAttribute)reader.GetInt32(0);
            var minimum = reader.GetDouble(1);
            var maximum = reader.GetDouble(2);
            await Assert.That(_data.Clamp(attribute, minimum - 1)).IsEqualTo(minimum);
            await Assert.That(_data.Clamp(attribute, maximum + 1)).IsEqualTo(maximum);
            count++;
        }
        await Assert.That(count).IsEqualTo(19);
        var unit = new Unit();
        unit.AddBonus(1, Modifier(UnitAttribute.MoveSpeedMul, 10000));
        await Assert.That(unit.MoveSpeedMul).IsEqualTo(4f);
    }

    private static void InstallFormulas(Func<UnitFormulaKind, double> value)
    {
        var manager = new FormulaManager();
        var formulas = new Dictionary<FormulaOwnerType, Dictionary<UnitFormulaKind, UnitFormula>>();
        foreach (var owner in Enum.GetValues<FormulaOwnerType>())
        {
            formulas[owner] = [];
            foreach (var kind in Enum.GetValues<UnitFormulaKind>())
            {
                var formula = new UnitFormula();
                Func<Dictionary<string, double>, double> expression = _ => value(kind);
                typeof(Formula).GetProperty("Expression", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(formula, expression);
                formulas[owner][kind] = formula;
            }
        }
        typeof(FormulaManager).GetField("_unitFormulas", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, formulas);
        typeof(FormulaManager).GetField("_unitVariables", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager,
            new Dictionary<uint, Dictionary<UnitFormulaVariableType, Dictionary<uint, UnitFormulaVariable>>>());
        s_formulaInstance.SetValue(null, manager);
    }

    private static SqliteConnection CreateData(string rows)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unit_attribute_limits(unit_attribute_id INTEGER, minimum INTEGER, maximum INTEGER); " +
                              "INSERT INTO unit_attribute_limits VALUES " + rows;
        command.ExecuteNonQuery();
        return connection;
    }

    private static Bonus Modifier(UnitAttribute attribute, int value, UnitModifierType type = UnitModifierType.Value) => new()
    {
        Template = new BonusTemplate { Attribute = attribute, ModifierType = type }, Value = value
    };

    private static DynamicBonus DynamicModifier(Unit unit, int value, UnitModifierType type) => new()
    {
        Template = new DynamicBonusTemplate { Attribute = UnitAttribute.MoveSpeedMul, ModifierType = type, FuncType = "LinearFunc" },
        SourceBuff = new Buff(unit, unit, new SkillCasterUnit(1), new BuffTemplate { Id = 1 }, null, DateTime.UtcNow),
        LinearFunc = new LinearFuncTemplate { StartValue = value, EndValue = value }
    };
}
