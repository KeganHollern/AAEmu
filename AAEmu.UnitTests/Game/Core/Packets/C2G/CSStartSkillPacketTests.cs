using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

public class CSStartSkillPacketTests
{
    [Test]
    public async Task TryAuthorizeComboFollowup_SpoofedCasterIsRejectedWithoutConsumingStage()
    {
        var state = new SkillComboState();
        state.Arm(23646, 60_000);

        await Assert.That(CSStartSkillPacket.TryAuthorizeComboFollowup(state, 23646, false)).IsFalse();
        await Assert.That(CSStartSkillPacket.TryAuthorizeComboFollowup(state, 23646, true)).IsTrue();
    }

    [Test]
    public async Task TryAuthorizeComboFollowup_OwnCasterCanConsumeStageOnlyOnce()
    {
        var state = new SkillComboState();
        state.Arm(23646, 60_000);

        await Assert.That(CSStartSkillPacket.TryAuthorizeComboFollowup(state, 23646, true)).IsTrue();
        await Assert.That(CSStartSkillPacket.TryAuthorizeComboFollowup(state, 23646, true)).IsFalse();
    }

    [Test]
    public async Task CanUseUnlearnedSkill_PlayerAbilitySkillIsRejected()
    {
        var template = new SkillTemplate
        {
            Id = 23587,
            AbilityId = AbilityType.Fight,
            NeedLearn = true
        };

        await Assert.That(CSStartSkillPacket.CanUseUnlearnedSkill(template)).IsFalse();
    }

    [Test]
    [Arguments(AbilityType.General, true)]
    [Arguments(AbilityType.None, true)]
    [Arguments(AbilityType.Fight, false)]
    public async Task CanUseUnlearnedSkill_NonLearnedTemplateRemainsAvailable(
        AbilityType ability,
        bool needLearn)
    {
        var template = new SkillTemplate
        {
            Id = 100,
            AbilityId = ability,
            NeedLearn = needLearn
        };

        await Assert.That(CSStartSkillPacket.CanUseUnlearnedSkill(template)).IsTrue();
    }
}
