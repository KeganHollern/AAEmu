using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class ExperienceModifierTests
{
    [Test]
    public async Task ScrollBuff_ChangesRealUnitBonusesAndAddsTwentyPercent()
    {
        var character = new CharacterMock();
        var template = new BuffTemplate { Id = 3084 };
        template.Bonuses.Add(new BonusTemplate { Attribute = UnitAttribute.ExpMul, Value = 20 });
        var buff = new Buff(character, character, new SkillCasterUnit(1), template, null, DateTime.UtcNow)
            { Index = 10, Passive = true, AbLevel = 1 };
        template.Start(character, character, buff);
        await Assert.That(character.ScaleExperienceGain(100, 1)).IsEqualTo(120);
        await Assert.That(character.ScaleExperienceGain(100, 2)).IsEqualTo(240);
        character.RemoveBonus(10, UnitAttribute.ExpMul);
        await Assert.That(character.ScaleExperienceGain(100, 1)).IsEqualTo(100);
    }

    [Test]
    [Arguments(0, 100)] [Arguments(20, 120)] [Arguments(400, 500)]
    [Arguments(500, 500)] [Arguments(1000, 500)] [Arguments(-500, 0)]
    public async Task ExperienceAttribute_UsesTheAuthoredZeroToFiveHundredPercentLimit(int bonus, int expected)
    {
        var character = new CharacterMock();
        character.AddBonus(1, Modifier(UnitAttribute.ExpMul, bonus));
        await Assert.That(character.ScaleExperienceGain(100, 1)).IsEqualTo(expected);
        await Assert.That(character.ScaleExperienceGain(-100, 1)).IsEqualTo(-100);
    }

    [Test]
    [Arguments(100, 240)] [Arguments(200, 360)]
    public async Task LaborBuff_AppliesOnlyToLaborAndComposesWithScroll(int laborBonus, int expected)
    {
        var character = new CharacterMock();
        character.AddBonus(1, Modifier(UnitAttribute.ExpMul, 20));
        character.AddBonus(2, Modifier(UnitAttribute.ExpByLaborPowerMul, laborBonus));
        await Assert.That(character.ScaleExperienceGain(100, 1)).IsEqualTo(120);
        await Assert.That(character.ScaleExperienceGain(100, 1, labor: true)).IsEqualTo(expected);
    }

    [Test]
    public async Task NpcPenalty_UsesTheSameAuthoredLowerLimit()
    {
        var npc = new Npc();
        npc.AddBonus(1, Modifier(UnitAttribute.ExpMul, -500));
        await Assert.That(npc.ExperienceMultiplier).IsEqualTo(0d);
    }

    [Test]
    public async Task LimitLoader_AppliesOnlyTheLimitsThatExistInCompact()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unit_attribute_limits(unit_attribute_id INTEGER,minimum INTEGER,maximum INTEGER); INSERT INTO unit_attribute_limits VALUES(95,0,500)";
        command.ExecuteNonQuery();
        var data = new ExperienceModifierGameData();
        data.Load(connection);
        await Assert.That(data.Clamp(UnitAttribute.ExpMul, 600)).IsEqualTo(500d);
        await Assert.That(data.Clamp(UnitAttribute.ExpByLaborPowerMul, 600)).IsEqualTo(600d);
        command.CommandText = "INSERT INTO unit_attribute_limits VALUES(186,0,400)";
        command.ExecuteNonQuery();
        data.Load(connection);
        await Assert.That(data.Clamp(UnitAttribute.ExpByLaborPowerMul, 600)).IsEqualTo(400d);
    }

    [Test]
    public async Task LargePositiveGain_StopsAtTheRepresentableAmount()
    {
        var character = new CharacterMock();
        character.AddBonus(1, Modifier(UnitAttribute.ExpMul, 1000));
        await Assert.That(character.ScaleExperienceGain(int.MaxValue, 2)).IsEqualTo(int.MaxValue);
    }

    private static Bonus Modifier(UnitAttribute attribute, int value) => new()
    {
        Template = new BonusTemplate { Attribute = attribute, Value = value }, Value = value
    };
}
