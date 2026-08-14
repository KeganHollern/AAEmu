using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class CharacterSkillsTests
{
    [Test]
    public async Task CanLearnPlayerSelectedSkill_AllRequirementsMet_ReturnsTrue()
    {
        var template = CreateTemplate();

        var result = CharacterSkills.CanLearnPlayerSelectedSkill(
            template,
            AbilityType.Fight,
            AbilityType.Magic,
            AbilityType.Love,
            abilityLevel: 20,
            availableSkillPoints: 1);

        await Assert.That(result).IsTrue();
    }

    [Test]
    [Arguments(AbilityType.General)]
    [Arguments(AbilityType.None)]
    [Arguments(AbilityType.Death)]
    public async Task CanLearnPlayerSelectedSkill_InvalidOrUnselectedTree_ReturnsFalse(AbilityType ability)
    {
        var template = CreateTemplate();
        template.AbilityId = ability;

        var result = CharacterSkills.CanLearnPlayerSelectedSkill(
            template,
            AbilityType.Fight,
            AbilityType.Magic,
            AbilityType.Love,
            abilityLevel: 20,
            availableSkillPoints: 1);

        await Assert.That(result).IsFalse();
    }

    [Test]
    [Arguments(false, true, 20, 1)]
    [Arguments(true, false, 20, 1)]
    [Arguments(true, true, 9, 1)]
    [Arguments(true, true, 20, 0)]
    public async Task CanLearnPlayerSelectedSkill_UnmetTemplateRequirement_ReturnsFalse(
        bool show,
        bool needLearn,
        int abilityLevel,
        int availableSkillPoints)
    {
        var template = CreateTemplate();
        template.Show = show;
        template.NeedLearn = needLearn;

        var result = CharacterSkills.CanLearnPlayerSelectedSkill(
            template,
            AbilityType.Fight,
            AbilityType.Magic,
            AbilityType.Love,
            abilityLevel,
            availableSkillPoints);

        await Assert.That(result).IsFalse();
    }

    private static SkillTemplate CreateTemplate()
    {
        return new SkillTemplate
        {
            Id = 100,
            Show = true,
            NeedLearn = true,
            AbilityId = AbilityType.Magic,
            AbilityLevel = 10,
            SkillPoints = 1
        };
    }
}
