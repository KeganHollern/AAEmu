using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class MailTests
{
    private CharacterMock _character;
    private CharacterMock _sender;
    private CharacterMails _mails;
    private MailManager _mailManager;
    private Mock<IHousingManager> _housingManager;
    private Mock<IWorldManager> _worldManager;
    private Mock<ISession> _recipientSession;
    private Mock<ISession> _senderSession;

    [Before(Test)]
    public void Setup()
    {
        _recipientSession = Mock.Of<ISession>();
        _senderSession = Mock.Of<ISession>();

        _character = new CharacterMock
        {
            AccountId = 1,
            Id = 1,
            Name = "tester",
            Money = 1000,
            Connection = new GameConnection(_recipientSession.Object)
        };
        _sender = new CharacterMock
        {
            AccountId = 2,
            Id = 2,
            Name = "sender",
            Connection = new GameConnection(_senderSession.Object)
        };

        _mails = new CharacterMails(_character);
        _character.Mails = _mails;
        _character.Connection.ActiveChar = _character;
        _sender.Connection.ActiveChar = _sender;

        var nameManager = new NameManager();
        nameManager.Load([], [], []);
        nameManager.AddCharacter(_character.Id, _character.Name, 1);
        nameManager.AddCharacter(_sender.Id, _sender.Name, 2);

        var mailIdManager = new MailIdManager();
        mailIdManager.Initialize();

        _worldManager = Mock.Of<IWorldManager>();
        _worldManager.GetCharacterById(_sender.Id).Returns(_sender);
        _worldManager.GetCharacter(_character.Name).Returns(_character);
        _housingManager = Mock.Of<IHousingManager>();

        _mailManager = new MailManager(
            mailIdManager,
            nameManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            _worldManager.Object,
            new Lazy<IHousingManager>(() => _housingManager.Object),
            Mock.Of<ILocalizationManager>().Object);

        new FeaturesManager(Mock.Of<IExperienceManager>().Object).Initialize();
        FeaturesManager.Fsets.Set(Feature.taxItem, false);

        // Reset singleton caches so Instance properties resolve via ServiceProvider
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var services = new ServiceCollection();
        services.AddSingleton(_mailManager);
        services.AddSingleton(nameManager);
        SingletonContainer.ServiceProvider = services.BuildServiceProvider();

        _mailManager._allPlayerMails = [];
    }

    [After(Test)]
    public void Teardown()
    {
        _mailManager._allPlayerMails = null;
        _character = null;
        _sender = null;
        _mails = null;
        _mailManager = null;
        _housingManager = null;
        _worldManager = null;
        _recipientSession = null;
        _senderSession = null;

        FeaturesManager.Fsets.Set(Feature.taxItem, true);

        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    [Test]
    public async Task MoneyTest()
    {
        var type = MailType.Express;
        var receiverCharName = "tester".NormalizeName();
        var title = "test";
        var text = "test";
        var attachments = (byte)0;
        var money0 = 500;
        var money1 = 0;
        var money2 = 0;
        var extra = 0;
        var itemSlots = new List<(SlotType slotType, byte slot)>();

        await Assert.That(_mails.SendMailToPlayer(type, receiverCharName, title, text, attachments, money0, money1, money2, extra, itemSlots)).IsEqualTo(MailResult.Success);
        await Assert.That(_character.Money).IsEqualTo(400);
    }

    [Test]
    public async Task PlayerNotFoundTest()
    {

        var type = MailType.Express;
        var receiverCharName = "bob";
        var title = "test";
        var text = "test";
        var attachments = (byte)0;
        var money0 = 500;
        var money1 = 0;
        var money2 = 0;
        var extra = 0;
        var itemSlots = new List<(SlotType slotType, byte slot)>();

        await Assert.That(_mails.SendMailToPlayer(type, receiverCharName, title, text, attachments, money0, money1, money2, extra, itemSlots)).IsNotEqualTo(MailResult.Success);
        await Assert.That(_character.Money).IsEqualTo(1000);
    }

    [Test]
    public async Task ReadMail_FirstRecipientRead_NotifiesOnlineSenderOnce()
    {
        var mail = AddReceivedMail();

        _mails.ReadMail(false, mail.Id);
        _mails.ReadMail(false, mail.Id);

        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(mail.OpenDate).IsGreaterThan(DateTime.UnixEpoch);
        _senderSession.SendPacket(Is<byte[]>(packet => IsPacket(packet, SCOffsets.SCMailReceiverOpenedPacket, mail.Id)))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task GetAttached_FirstClaim_SetsOpenDateAndNotifiesOnlineSenderOnce()
    {
        var mail = AddReceivedMail(copperCoins: 25);
        var moneyBeforeClaim = _character.Money;

        var result = _mails.GetAttached(mail.Id, true, false, true);
        _mails.GetAttached(mail.Id, true, false, true);

        await Assert.That(result).IsTrue();
        await Assert.That(_character.Money).IsEqualTo(moneyBeforeClaim + 25);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(mail.OpenDate).IsGreaterThan(DateTime.UnixEpoch);
        _senderSession.SendPacket(Is<byte[]>(packet => IsPacket(packet, SCOffsets.SCMailReceiverOpenedPacket, mail.Id)))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task DeleteMail_ReceivedMail_NotifiesOnlineSenderAndRemovesMail()
    {
        var mail = AddReceivedMail(status: MailStatus.Read);

        _mails.DeleteMail(mail.Id, false);

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsFalse();
        _senderSession.SendPacket(Is<byte[]>(packet => IsMailRemovedPacket(packet, mail.Id)))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task TakeAllAttachment_ClaimsThenRemovesMailAndSendsBothReceipts()
    {
        var mail = AddReceivedMail(copperCoins: 25);
        var stream = new PacketStream().Write(mail.Id);
        stream.Rollback();

        var packet = new CSTakeAllAttachmentItemPacket { Connection = _character.Connection };
        packet.Read(stream);

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsFalse();
        _senderSession.SendPacket(Is<byte[]>(data => IsPacket(data, SCOffsets.SCMailReceiverOpenedPacket, mail.Id)))
            .WasCalled(Times.Once);
        _senderSession.SendPacket(Is<byte[]>(data => IsMailRemovedPacket(data, mail.Id)))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task OfflineSender_ReadAndDelete_UsesStoredMailStateWithoutPackets()
    {
        _worldManager.GetCharacterById(_sender.Id).Returns((Character)null);
        var mail = AddReceivedMail();

        _mails.ReadMail(false, mail.Id);

        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        await Assert.That(mail.OpenDate).IsGreaterThan(DateTime.UnixEpoch);
        await Assert.That(mail.IsDirty).IsTrue();

        _mails.DeleteMail(mail.Id, false);

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsFalse();
        _senderSession.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task PayChargeMoney_ValidTaxMail_ChargesOnceAndOffersNextPeriod()
    {
        var house = CreateTaxHouse();
        ConfigureTaxPayment(house, 100);
        var mail = AddTaxMail(house, 100);
        BaseMail successorMail = null;
        _housingManager.OfferTaxPrepayment(house)
            .Callback(() => successorMail = AddTaxMail(house, 100));

        var firstResult = _mailManager.PayChargeMoney(_character, mail.Id, false);
        var secondResult = _mailManager.PayChargeMoney(_character, mail.Id, false);

        await Assert.That(firstResult).IsTrue();
        await Assert.That(secondResult).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(900);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsFalse();
        await Assert.That(successorMail).IsNotNull();
        await Assert.That(successorMail.Id).IsNotEqualTo(mail.Id);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(successorMail.Id)).IsTrue();
        _housingManager.PayWeeklyTax(house).WasCalled(Times.Once);
        _housingManager.OfferTaxPrepayment(house).WasCalled(Times.Once);
    }

    [Test]
    public async Task PayChargeMoney_MailOwnedByAnotherCharacter_IsRejected()
    {
        var house = CreateTaxHouse();
        ConfigureTaxPayment(house, 100);
        var mail = AddTaxMail(house, 100);
        _sender.Money = 1000;

        var result = _mailManager.PayChargeMoney(_sender, mail.Id, false);

        await Assert.That(result).IsFalse();
        await Assert.That(_sender.Money).IsEqualTo(1000);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsTrue();
        _housingManager.PayWeeklyTax(house).WasCalled(Times.Never);
    }

    [Test]
    public async Task PayChargeMoney_StaleTaxAmount_ReissuesWithoutCharging()
    {
        var house = CreateTaxHouse();
        ConfigureTaxPayment(house, 200);
        var mail = AddTaxMail(house, 100);
        BaseMail replacementMail = null;
        _housingManager.OfferTaxPrepayment(house)
            .Callback(() => replacementMail = AddTaxMail(house, 200));

        var firstResult = _mailManager.PayChargeMoney(_character, mail.Id, false);
        var replayResult = _mailManager.PayChargeMoney(_character, mail.Id, false);

        await Assert.That(firstResult).IsFalse();
        await Assert.That(replayResult).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(1000);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsFalse();
        await Assert.That(replacementMail).IsNotNull();
        await Assert.That(replacementMail.Id).IsNotEqualTo(mail.Id);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(replacementMail.Id)).IsTrue();
        _housingManager.PayWeeklyTax(house).WasCalled(Times.Never);
        _housingManager.OfferTaxPrepayment(house).WasCalled(Times.Once);
    }

    [Test]
    public async Task PayChargeMoney_BeyondPrepaymentLimit_RemovesOfferWithoutCharging()
    {
        var house = CreateTaxHouse();
        _housingManager.GetHouseById(house.Id).Returns(house);
        _housingManager.CanPayTaxMail(house).Returns(false);
        var mail = AddTaxMail(house, 100);

        var result = _mailManager.PayChargeMoney(_character, mail.Id, false);

        await Assert.That(result).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(1000);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsFalse();
        _housingManager.PayWeeklyTax(house).WasCalled(Times.Never);
        _housingManager.OfferTaxPrepayment(house).WasCalled(Times.Never);
    }

    [Test]
    public async Task PayChargeMoney_DeadlineExtensionFails_DoesNotCharge()
    {
        var house = CreateTaxHouse();
        ConfigureTaxPayment(house, 100);
        _housingManager.PayWeeklyTax(house).Returns(false);
        var mail = AddTaxMail(house, 100);

        var result = _mailManager.PayChargeMoney(_character, mail.Id, false);

        await Assert.That(result).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(1000);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsTrue();
        _housingManager.PayWeeklyTax(house).WasCalled(Times.Once);
        _housingManager.OfferTaxPrepayment(house).WasCalled(Times.Never);
    }

    [Test]
    public async Task PayChargeMoney_DuplicateTaxMails_ExtendsOnlyOnceAndRemovesDuplicates()
    {
        var house = CreateTaxHouse();
        ConfigureTaxPayment(house, 100);
        var firstMail = AddTaxMail(house, 100);
        AddTaxMail(house, 100);

        var result = _mailManager.PayChargeMoney(_character, firstMail.Id, false);

        await Assert.That(result).IsTrue();
        await Assert.That(_character.Money).IsEqualTo(900);
        await Assert.That(_mailManager.GetMyHouseMails(house.Id)).IsEmpty();
        _housingManager.PayWeeklyTax(house).WasCalled(Times.Once);
        _housingManager.OfferTaxPrepayment(house).WasCalled(Times.Once);
    }

    [Test]
    public async Task PayChargeMoney_NonTaxBillingMail_IsRejected()
    {
        var mail = new BaseMail
        {
            Id = _mailManager.GetNewMailId(),
            MailType = MailType.Billing,
            ReceiverName = _character.Name,
            Header =
            {
                ReceiverId = _character.Id,
                SenderName = ".userBill"
            },
            Body =
            {
                BillingAmount = 100
            }
        };
        _mailManager.AllPlayerMails.Add(mail.Id, mail);

        var result = _mailManager.PayChargeMoney(_character, mail.Id, false);

        await Assert.That(result).IsFalse();
        await Assert.That(_character.Money).IsEqualTo(1000);
        _housingManager.GetHouseById(Any<uint>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task TryConsumeTaxCertificates_MixedCertificatePayment_ConsumesBoundFirst()
    {
        var boundConsumed = 0;
        var taxConsumed = 0;

        var result = MailManager.TryConsumeTaxCertificates(
            5,
            2,
            3,
            count => boundConsumed += count,
            count => taxConsumed += count,
            _ => true,
            _ => true,
            out var fullyRestored);

        await Assert.That(result).IsTrue();
        await Assert.That(fullyRestored).IsTrue();
        await Assert.That(boundConsumed).IsEqualTo(2);
        await Assert.That(taxConsumed).IsEqualTo(3);
    }

    [Test]
    public async Task TryConsumeTaxCertificates_InsufficientCertificates_DoesNotConsume()
    {
        var consumeCalls = 0;

        var result = MailManager.TryConsumeTaxCertificates(
            5,
            1,
            3,
            count => { consumeCalls++; return count; },
            count => { consumeCalls++; return count; },
            _ => true,
            _ => true,
            out var fullyRestored);

        await Assert.That(result).IsFalse();
        await Assert.That(fullyRestored).IsTrue();
        await Assert.That(consumeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task TryConsumeTaxCertificates_PartialConsumption_RestoresRemovedCertificates()
    {
        var restoredBoundCerts = 0;
        var restoredTaxCerts = 0;

        var result = MailManager.TryConsumeTaxCertificates(
            5,
            2,
            3,
            _ => 1,
            _ => 2,
            count => { restoredBoundCerts += count; return true; },
            count => { restoredTaxCerts += count; return true; },
            out var fullyRestored);

        await Assert.That(result).IsFalse();
        await Assert.That(fullyRestored).IsTrue();
        await Assert.That(restoredBoundCerts).IsEqualTo(1);
        await Assert.That(restoredTaxCerts).IsEqualTo(2);
    }

    private BaseMail AddReceivedMail(
        long mailId = 100,
        MailStatus status = MailStatus.Unread,
        int copperCoins = 0)
    {
        var mail = new BaseMail
        {
            Id = mailId,
            MailType = MailType.Express,
            Title = "Receipt test",
            ReceiverName = _character.Name,
            Header =
            {
                Status = status,
                SenderId = _sender.Id,
                SenderName = _sender.Name,
                ReceiverId = _character.Id,
                Attachments = copperCoins > 0 ? (byte)1 : (byte)0
            },
            Body =
            {
                CopperCoins = copperCoins,
                SendDate = DateTime.UtcNow,
                RecvDate = DateTime.UtcNow
            }
        };
        mail.IsDirty = false;
        _mailManager.AllPlayerMails.Add(mail.Id, mail);
        return mail;
    }

    private House CreateTaxHouse()
    {
        return new House
        {
            Id = 42,
            AccountId = _character.AccountId,
            OwnerId = _character.Id,
            ProtectionEndDate = DateTime.UtcNow.AddDays(14)
        };
    }

    private void ConfigureTaxPayment(House house, int currentAmount)
    {
        _housingManager.GetHouseById(house.Id).Returns(house);
        _housingManager.CanPayTaxMail(house).Returns(true);
        _housingManager.GetWeeklyTaxAmount(house).Returns(currentAmount);
        _housingManager.PayWeeklyTax(house).Returns(true);
    }

    private BaseMail AddTaxMail(House house, int amount)
    {
        var mail = new BaseMail
        {
            Id = _mailManager.GetNewMailId(),
            MailType = MailType.Billing,
            ReceiverName = _character.Name,
            Header =
            {
                Status = MailStatus.Unpaid,
                SenderId = 0,
                SenderName = MailForTax.TaxSenderName,
                ReceiverId = _character.Id,
                Extra = house.Id
            },
            Body =
            {
                BillingAmount = amount,
                SendDate = DateTime.UtcNow,
                RecvDate = DateTime.UtcNow
            }
        };
        _mailManager.AllPlayerMails.Add(mail.Id, mail);
        _character.Mails.UnreadMailCount.UpdateReceived(MailType.Billing, 1);
        return mail;
    }

    private static bool IsPacket(byte[] packet, ushort opcode, long mailId)
    {
        return packet.Length >= 16 &&
               packet[6] == (byte)opcode &&
               packet[7] == (byte)(opcode >> 8) &&
               BitConverter.ToInt64(packet, 8) == mailId;
    }

    private static bool IsMailRemovedPacket(byte[] packet, long mailId)
    {
        return packet.Length >= 17 &&
               packet[6] == (byte)(SCOffsets.SCMailRemovedPacket & 0xff) &&
               packet[7] == (byte)(SCOffsets.SCMailRemovedPacket >> 8) &&
               packet[8] == 1 &&
               BitConverter.ToInt64(packet, 9) == mailId;
    }
}
