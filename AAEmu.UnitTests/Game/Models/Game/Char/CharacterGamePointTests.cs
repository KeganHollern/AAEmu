using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class CharacterGamePointTests
{
    [Test]
    public async Task TrySpendGamePoints_VocationBonusesActive_DeductsExactAmount()
    {
        var character = new CharacterMock { VocationPoint = 1_000 };
        character.AddBonus(100, CreateBonus(UnitAttribute.LivingPointGain, UnitModifierType.Value, 50));
        character.AddBonus(101, CreateBonus(UnitAttribute.LivingPointGainMul, UnitModifierType.Value, 100));

        character.ChangeGamePoints(GamePointKind.Vocation, 100);
        var balanceAfterEarning = character.VocationPoint;
        var spent = character.TrySpendGamePoints(GamePointKind.Vocation, 120);

        await Assert.That(balanceAfterEarning).IsEqualTo(1_300);
        await Assert.That(spent).IsTrue();
        await Assert.That(character.VocationPoint).IsEqualTo(1_180);
    }

    [Test]
    public async Task ChangeGamePoints_NegativeVocationChange_DoesNotApplyEarningBonuses()
    {
        var character = new CharacterMock { VocationPoint = 1_000 };
        character.AddBonus(100, CreateBonus(UnitAttribute.LivingPointGain, UnitModifierType.Value, 50));
        character.AddBonus(101, CreateBonus(UnitAttribute.LivingPointGainMul, UnitModifierType.Value, 100));

        character.ChangeGamePoints(GamePointKind.Vocation, -120);

        await Assert.That(character.VocationPoint).IsEqualTo(880);
    }

    [Test]
    public async Task TrySpendGamePoints_InsufficientBalance_DoesNotChangeBalance()
    {
        var character = new CharacterMock { HonorPoint = 119 };

        var spent = character.TrySpendGamePoints(GamePointKind.Honor, 120);

        await Assert.That(spent).IsFalse();
        await Assert.That(character.HonorPoint).IsEqualTo(119);
    }

    private static Bonus CreateBonus(
        UnitAttribute attribute,
        UnitModifierType modifierType,
        int value)
    {
        return new Bonus
        {
            Template = new BonusTemplate
            {
                Attribute = attribute,
                ModifierType = modifierType,
                Value = value
            },
            Value = value
        };
    }
}
