using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed class CraftDurationTests
{
    [Test]
    public async Task Craft4107_UsesItsAuthoredDelayWithoutChangingTheSharedSkill()
    {
        // r208022 crafts.id=4107, skill_id=15086, cast_delay=15000.
        var skill = new SkillTemplate { Id = 15086, CastingTime = 5000 };
        var craft = new Craft { Id = 4107, SkillId = skill.Id, CastDelay = 15000 };

        await Assert.That(CraftDuration.GetBaseMilliseconds(craft, skill)).IsEqualTo(15000);
        await Assert.That(CraftDuration.GetBaseMilliseconds(null, skill)).IsEqualTo(5000);
        await Assert.That(skill.CastingTime).IsEqualTo(5000);
    }

    [Test]
    public async Task CraftsSharingASkill_KeepSeparateDurations()
    {
        var skill = new SkillTemplate { CastingTime = 5000 };
        var shortCraft = new Craft { CastDelay = 500 };
        var longCraft = new Craft { CastDelay = 15000 };

        await Assert.That(CraftDuration.GetBaseMilliseconds(shortCraft, skill)).IsEqualTo(500);
        await Assert.That(CraftDuration.GetBaseMilliseconds(longCraft, skill)).IsEqualTo(15000);
        await Assert.That(CraftDuration.GetBaseMilliseconds(shortCraft, skill)).IsEqualTo(500);
        await Assert.That(skill.CastingTime).IsEqualTo(5000);
    }

    [Test]
    public async Task OrdinaryInstantSkill_KeepsItsZeroDuration()
    {
        await Assert.That(CraftDuration.GetBaseMilliseconds(null, new SkillTemplate())).IsEqualTo(0);
    }
}
