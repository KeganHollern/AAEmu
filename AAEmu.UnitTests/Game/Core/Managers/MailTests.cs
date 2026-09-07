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
    private CharacterMock _otherRecipient;
    private CharacterMock _sender;
    private CharacterMails _mails;
    private CharacterMails _otherRecipientMails;
    private CharacterMails _senderMails;
    private MailManager _mailManager;
    private Mock<IHousingManager> _housingManager;
    private Mock<IWorldManager> _worldManager;
    private Mock<ISession> _recipientSession;
    private Mock<ISession> _otherRecipientSession;
    private Mock<ISession> _senderSession;

    [Before(Test)]
    public void Setup()
    {
        _recipientSession = Mock.Of<ISession>();
        _otherRecipientSession = Mock.Of<ISession>();
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
            Name = "Sender",
            Connection = new GameConnection(_senderSession.Object)
        };
        _otherRecipient = new CharacterMock
        {
            AccountId = 3,
            Id = 3,
            Name = "otherRecipient",
            Money = 1000,
            Connection = new GameConnection(_otherRecipientSession.Object)
        };

        _mails = new CharacterMails(_character);
        _otherRecipientMails = new CharacterMails(_otherRecipient);
        _senderMails = new CharacterMails(_sender);
        _character.Mails = _mails;
        _otherRecipient.Mails = _otherRecipientMails;
        _sender.Mails = _senderMails;
        _character.Connection.ActiveChar = _character;
        _otherRecipient.Connection.ActiveChar = _otherRecipient;
        _sender.Connection.ActiveChar = _sender;

        var nameManager = new NameManager();
        nameManager.Load([], [], []);
        nameManager.AddCharacter(_character.Id, _character.Name, 1);
        nameManager.AddCharacter(_sender.Id, _sender.Name, 2);
        nameManager.AddCharacter(_otherRecipient.Id, _otherRecipient.Name, 3);

        var mailIdManager = new MailIdManager();
        mailIdManager.Initialize();

        _worldManager = Mock.Of<IWorldManager>();
        _worldManager.GetCharacterById(_character.Id).Returns(_character);
        _worldManager.GetCharacterById(_otherRecipient.Id).Returns(_otherRecipient);
        _worldManager.GetCharacterById(_sender.Id).Returns(_sender);
        _worldManager.GetCharacter(_character.Name).Returns(_character);
        _worldManager.GetCharacter(_otherRecipient.Name).Returns(_otherRecipient);
        _worldManager.GetCharacter(_sender.Name).Returns(_sender);
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
        _otherRecipient = null;
        _sender = null;
        _mails = null;
        _otherRecipientMails = null;
        _senderMails = null;
        _mailManager = null;
        _housingManager = null;
        _worldManager = null;
        _recipientSession = null;
        _otherRecipientSession = null;
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
    public async Task ReadMail_ReceivedMailOwnedByOtherRecipient_DoesNotDiscloseOrMutate()
    {
        var mail = AddReceivedMail();
        _otherRecipientMails.UnreadMailCount.UpdateReceived(mail.MailType, 3);

        _otherRecipientMails.ReadMail(false, mail.Id);

        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(mail.OpenDate).IsEqualTo(default(DateTime));
        await Assert.That(mail.IsDelivered).IsFalse();
        await Assert.That(mail.IsDirty).IsFalse();
        await Assert.That(_otherRecipientMails.UnreadMailCount.Received).IsEqualTo(3);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailBodyPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailStatusUpdatedPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCCountUnreadMailPacket)))
            .WasCalled(Times.Never);
        _senderSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailReceiverOpenedPacket)))
            .WasCalled(Times.Never);

        _mails.ReadMail(false, mail.Id);

        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Read);
        _recipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailBodyPacket)))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task ReadMail_SentMailOwnedByOtherSender_DoesNotDisclose()
    {
        var mail = AddReceivedMail();

        _otherRecipientMails.ReadMail(true, mail.Id);

        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailBodyPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailStatusUpdatedPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCCountUnreadMailPacket)))
            .WasCalled(Times.Never);

        _senderMails.ReadMail(true, mail.Id);

        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        _senderSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailBodyPacket)))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task GetAttached_MailOwnedByOtherRecipient_DoesNotClaimMoneyOrItems()
    {
        var mail = AddReceivedMail(copperCoins: 25);
        mail.Body.Attachments.Add(new ItemMock(777)
        {
            SlotType = SlotType.Mail,
            Slot = 0
        });
        mail.Header.Attachments = 2;
        mail.IsDirty = false;
        _otherRecipientMails.UnreadMailCount.UpdateReceived(mail.MailType, 3);
        var moneyBeforeClaim = _otherRecipient.Money;

        var result = _otherRecipientMails.GetAttached(mail.Id, true, true, true);

        await Assert.That(result).IsFalse();
        await Assert.That(_otherRecipient.Money).IsEqualTo(moneyBeforeClaim);
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(25);
        await Assert.That(mail.Body.Attachments.Count).IsEqualTo(1);
        await Assert.That(mail.Body.Attachments[0].Id).IsEqualTo(777ul);
        await Assert.That(mail.Header.Attachments).IsEqualTo((byte)2);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(mail.OpenDate).IsEqualTo(default(DateTime));
        await Assert.That(mail.IsDirty).IsFalse();
        await Assert.That(_otherRecipientMails.UnreadMailCount.Received).IsEqualTo(3);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCAttachmentTakenPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailStatusUpdatedPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCCountUnreadMailPacket)))
            .WasCalled(Times.Never);
        _senderSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailReceiverOpenedPacket)))
            .WasCalled(Times.Never);
    }

    [Test]
    public async Task TakeAllAttachment_MailOwnedByOtherRecipient_DoesNotSendStatusOrDelete()
    {
        var mail = AddReceivedMail(copperCoins: 25);
        _otherRecipientMails.UnreadMailCount.UpdateReceived(mail.MailType, 3);
        var moneyBeforeClaim = _otherRecipient.Money;
        var stream = new PacketStream().Write(mail.Id);
        stream.Rollback();

        var packet = new CSTakeAllAttachmentItemPacket { Connection = _otherRecipient.Connection };
        packet.Read(stream);

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsTrue();
        await Assert.That(_otherRecipient.Money).IsEqualTo(moneyBeforeClaim);
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(25);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(_otherRecipientMails.UnreadMailCount.Received).IsEqualTo(3);
        _otherRecipientSession.SendPacket(Is<byte[]>(data => HasOpcode(data, SCOffsets.SCAttachmentTakenPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(data => HasOpcode(data, SCOffsets.SCMailStatusUpdatedPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(data => HasOpcode(data, SCOffsets.SCCountUnreadMailPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(data => HasOpcode(data, SCOffsets.SCMailDeletedPacket)))
            .WasCalled(Times.Never);
        _senderSession.SendPacket(Is<byte[]>(data => HasOpcode(data, SCOffsets.SCMailReceiverOpenedPacket)))
            .WasCalled(Times.Never);
        _senderSession.SendPacket(Is<byte[]>(data => HasOpcode(data, SCOffsets.SCMailRemovedPacket)))
            .WasCalled(Times.Never);
    }

    [Test]
    public async Task DeleteMail_MailOwnedByOtherRecipient_DoesNotDeleteOrNotify()
    {
        var mail = AddReceivedMail();
        _otherRecipientMails.UnreadMailCount.UpdateReceived(mail.MailType, 3);

        _otherRecipientMails.DeleteMail(mail.Id, false);

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsTrue();
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(_otherRecipientMails.UnreadMailCount.Received).IsEqualTo(3);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailDeletedPacket)))
            .WasCalled(Times.Never);
        _senderSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailRemovedPacket)))
            .WasCalled(Times.Never);
    }

    [Test]
    public async Task ReturnMail_MailOwnedByOtherRecipient_DoesNotReturnOrDelete()
    {
        var mail = AddReceivedMail();
        mail.Body.Text = "Original body";
        mail.IsDirty = false;
        var moneyBeforeReturn = _otherRecipient.Money;

        _otherRecipientMails.ReturnMail(mail.Id);

        await Assert.That(_mailManager.AllPlayerMails.Count).IsEqualTo(1);
        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsTrue();
        await Assert.That(_otherRecipient.Money).IsEqualTo(moneyBeforeReturn);
        await Assert.That(mail.Header.SenderId).IsEqualTo(_sender.Id);
        await Assert.That(mail.Header.ReceiverId).IsEqualTo(_character.Id);
        await Assert.That(mail.Header.Status).IsEqualTo(MailStatus.Unread);
        await Assert.That(mail.Body.Text).IsEqualTo("Original body");
        await Assert.That(mail.IsDirty).IsFalse();
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailSentPacket)))
            .WasCalled(Times.Never);
        _otherRecipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailDeletedPacket)))
            .WasCalled(Times.Never);
        _senderSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCGotMailPacket)))
            .WasCalled(Times.Never);
        _senderSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailRemovedPacket)))
            .WasCalled(Times.Never);
    }

    [Test]
    public async Task ReturnMail_OwningRecipient_ReturnsMail()
    {
        var mail = AddReceivedMail(status: MailStatus.Read);
        mail.Body.Text = "Return body";
        mail.IsDirty = false;
        var moneyBeforeReturn = _character.Money;

        _mails.ReturnMail(mail.Id);

        await Assert.That(_mailManager.AllPlayerMails.ContainsKey(mail.Id)).IsFalse();
        await Assert.That(_mailManager.AllPlayerMails.Count).IsEqualTo(1);
        var returnedMail = _mailManager.AllPlayerMails.Values.Single();
        await Assert.That(returnedMail.Id).IsNotEqualTo(mail.Id);
        await Assert.That(returnedMail.Header.SenderId).IsEqualTo(_character.Id);
        await Assert.That(returnedMail.Header.ReceiverId).IsEqualTo(_sender.Id);
        await Assert.That(returnedMail.Header.SenderName).IsEqualTo(_character.Name);
        await Assert.That(returnedMail.ReceiverName).IsEqualTo(_sender.Name);
        await Assert.That(returnedMail.Title).IsEqualTo(mail.Title);
        await Assert.That(returnedMail.Body.Text).IsEqualTo(mail.Body.Text);
        await Assert.That(_character.Money).IsEqualTo(moneyBeforeReturn - MailManager.CostExpress);
        _recipientSession.SendPacket(Is<byte[]>(packet => HasOpcode(packet, SCOffsets.SCMailSentPacket)))
            .WasCalled(Times.Once);
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

    private static bool HasOpcode(byte[] packet, ushort opcode)
    {
        return packet.Length >= 8 && BitConverter.ToUInt16(packet, 6) == opcode;
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
