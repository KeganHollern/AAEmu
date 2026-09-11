using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Mails;

namespace AAEmu.UnitTests.Game.Models.Game.Mails;

public class MailLifecyclePolicyTests
{
    [Test]
    [Arguments(MailType.Normal, false, true)]
    [Arguments(MailType.Express, false, true)]
    [Arguments(MailType.Normal, true, false)]
    [Arguments(MailType.Express, true, false)]
    [Arguments(MailType.Admin, false, false)]
    [Arguments(MailType.AucBidWin, false, false)]
    public async Task ReturnEligibility_OnlyUnreturnedPlayerMail(MailType type, bool returned, bool expected)
    {
        var mail = new BaseMail { MailType = type, IsDelivered = true,
            Header = { SenderId = 1, ReceiverId = 2, Returned = returned } };
        await Assert.That(mail.CanReturnMail()).IsEqualTo(expected);
    }

    [Test]
    public async Task Expiry_UsesDeliveryDateNotOpenDateOrNotificationState()
    {
        var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        var mail = new BaseMail { OpenDate = now, IsDelivered = false,
            Body = { SendDate = now.AddDays(-20), RecvDate = now.AddDays(-14) } };
        await Assert.That(MailManager.IsExpired(mail, now.AddTicks(-1))).IsFalse();
        await Assert.That(MailManager.IsExpired(mail, now)).IsTrue();
        mail.Header.Status = MailStatus.Read;
        await Assert.That(MailManager.IsExpired(mail, now)).IsTrue();
    }
}
