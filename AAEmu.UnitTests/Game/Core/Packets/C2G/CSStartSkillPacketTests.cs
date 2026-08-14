using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Skills;

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
}
