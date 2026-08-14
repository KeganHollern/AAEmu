using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public class SkillComboStateTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task ArmedFollowup_CanBeConsumedOnlyOnce()
    {
        var state = new SkillComboState();
        state.Arm(23646, 1000, Now);

        await Assert.That(state.TryConsume(23646, Now.AddMilliseconds(999))).IsTrue();
        await Assert.That(state.TryConsume(23646, Now.AddMilliseconds(999))).IsFalse();
    }

    [Test]
    public async Task WrongFollowup_DoesNotConsumeExpectedStage()
    {
        var state = new SkillComboState();
        state.Arm(14930, 1000, Now);

        await Assert.That(state.TryConsume(23646, Now.AddMilliseconds(500))).IsFalse();
        await Assert.That(state.TryConsume(14930, Now.AddMilliseconds(500))).IsTrue();
    }

    [Test]
    public async Task ExpiredFollowup_IsRejectedAndCleared()
    {
        var state = new SkillComboState();
        state.Arm(23649, 1000, Now);

        await Assert.That(state.TryConsume(23649, Now.AddMilliseconds(1001))).IsFalse();
        await Assert.That(state.TryConsume(23649, Now)).IsFalse();
    }
}
