using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed class SlaveAchievementTests
{
    [Test]
    public async Task CreateKillSlaveProgressEvent_UsesSlaveKindAndHostileSelector()
    {
        var template = new SlaveTemplate
        {
            Id = 999,
            SlaveKind = SlaveKind.SmallSailingShip
        };

        var progressEvent = Slave.CreateKillSlaveProgressEvent(template);

        await Assert.That(progressEvent.Kind).IsEqualTo(CharRecordKind.KillSlave);
        await Assert.That(progressEvent.Value1).IsEqualTo((uint)SlaveKind.SmallSailingShip);
        await Assert.That(progressEvent.Value2).IsEqualTo(1u);
        await Assert.That(progressEvent.Amount).IsEqualTo(1u);
    }
}
