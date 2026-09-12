using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class ServiceInteractionTests
{
    private static readonly FieldInfo s_worldInstance = typeof(Singleton<WorldManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previous;
    private WorldInstance _world;
    private CharacterMock _character;
    private Npc _npc;

    [Before(Test)]
    public void SetUp()
    {
        _previous = s_worldInstance.GetValue(null);
        var manager = new WorldManager(null, null, null, null, null);
        s_worldInstance.SetValue(null, manager);
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        ((ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!)[1] = _world;
        var connection = new GameConnection(Mock.Of<ISession>().Object);
        _character = new CharacterMock { Id = 7, ObjId = 70, Level = 50, ParentWorld = _world,
            Money = 100, Money2 = 100, Connection = connection };
        connection.ActiveChar = _character;
        _npc = new Npc { ObjId = 80, ParentWorld = _world,
            Template = new NpcTemplate { Banker = true, Auctioneer = true, Blacksmith = true } };
        _world.AddObject(_npc);
        _character.CurrentInteractionObject = _npc;
    }

    [After(Test)]
    public void TearDown() => s_worldInstance.SetValue(null, _previous);

    [Test]
    public async Task OpenBank_MustMatchLiveObjectAndInclusiveThreeDimensionalRange()
    {
        _npc.Transform.Local.SetPosition(0, 0, 5);
        await Assert.That(ServiceInteraction.CanUseBank(_character)).IsTrue();
        _npc.Transform.Local.SetPosition(0, 0, 5.01f);
        await Assert.That(ServiceInteraction.CanUseBank(_character)).IsFalse();
        _npc.Transform.Local.SetPosition(0, 0, 0);
        _world.RemoveObject(_npc);
        _world.AddObject(new Npc { ObjId = _npc.ObjId, ParentWorld = _world, Template = _npc.Template });
        await Assert.That(ServiceInteraction.CanUseBank(_character)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BankPackets_NoInteractionOrDistantBank_DoNotTransfer(bool distant)
    {
        if (distant)
            _npc.Transform.Local.SetPosition(6, 0, 0);
        else
            _character.CurrentInteractionObject = null;
        new CSDepositMoneyPacket { Connection = _character.Connection }.Read(new PacketStream().Write(10).Write(0));
        new CSWithdrawMoneyPacket { Connection = _character.Connection }.Read(new PacketStream().Write(20).Write(0));
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_character.Money2).IsEqualTo(100L);
    }

    [Test]
    [Arguments(int.MinValue, 0)]
    [Arguments(-1, 0)]
    [Arguments(0, 0)]
    [Arguments(1, 1)]
    public async Task BankPackets_InvalidAmounts_DoNotTransfer(int money, int points)
    {
        new CSDepositMoneyPacket { Connection = _character.Connection }.Read(new PacketStream().Write(money).Write(points));
        new CSWithdrawMoneyPacket { Connection = _character.Connection }.Read(new PacketStream().Write(money).Write(points));
        await Assert.That(_character.Money).IsEqualTo(100L);
        await Assert.That(_character.Money2).IsEqualTo(100L);
    }

    [Test]
    public async Task BankPackets_ExactBalances_MoveBothDirections()
    {
        new CSDepositMoneyPacket { Connection = _character.Connection }.Read(new PacketStream().Write(100).Write(0));
        await Assert.That(_character.Money).IsEqualTo(0L);
        await Assert.That(_character.Money2).IsEqualTo(200L);
        new CSWithdrawMoneyPacket { Connection = _character.Connection }.Read(new PacketStream().Write(200).Write(0));
        await Assert.That(_character.Money).IsEqualTo(200L);
        await Assert.That(_character.Money2).IsEqualTo(0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AuctionPacket_NoInteractionOrDistantAuctioneer_RejectsBeforeManagerUse(bool distant)
    {
        if (distant)
            _npc.Transform.Local.SetPosition(0, 0, 6);
        else
            _character.CurrentInteractionObject = null;
        // No AuctionManager exists in this fixture. The packet must stop before manager use.
        var packet = new CSAuctionPostPacket { Connection = _character.Connection };
        packet.Read(new PacketStream().WriteBc(_npc.ObjId).WriteBc(0U).Write(1UL).Write(10).Write(20).Write((byte)0));
        await Assert.That(_character.Money).IsEqualTo(100L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MailClaims_NoOpenMailboxOrDistantMailbox_RejectBeforeClaims(bool distant)
    {
        var mailbox = new Doodad { ObjId = 90, ParentWorld = _world };
        mailbox.CurrentFuncs.Add(new DoodadFunc { FuncType = nameof(DoodadFuncNaviOpenMailbox) });
        _world.AddObject(mailbox);
        new DoodadFuncNaviOpenMailbox().Use(_character, mailbox, 0);
        await Assert.That(ServiceInteraction.CanUseMailbox(_character)).IsTrue();
        if (distant)
            mailbox.Transform.Local.SetPosition(0, 0, 6);
        else
            _character.CurrentInteractionObject = null;
        // Mails is null. Each packet must reject before it enters the claim path.
        new CSTakeAttachmentMoneyPacket { Connection = _character.Connection }.Read(new PacketStream().Write(1L));
        new CSTakeAllAttachmentItemPacket { Connection = _character.Connection }.Read(new PacketStream().Write(1L));
        new CSTakeAttachmentSequentially { Connection = _character.Connection }.Read(new PacketStream().Write(1L));
        await Assert.That(_character.Money).IsEqualTo(100L);
    }

    [Test]
    public async Task DoodadUi_OnlyTheAuthoredServiceAndCurrentPhaseAreAllowed()
    {
        var bank = new Doodad { ObjId = 90, ParentWorld = _world };
        bank.CurrentFuncs.Add(new DoodadFunc { FuncType = nameof(DoodadFuncBankUi) });
        _world.AddObject(bank);
        new DoodadFuncBankUi().Use(_character, bank, 0);
        await Assert.That(ServiceInteraction.CanUseBank(_character)).IsTrue();
        await Assert.That(ServiceInteraction.CanUseAuction(_character)).IsFalse();
        await Assert.That(ServiceInteraction.CanUseMailbox(_character)).IsFalse();
        bank.CurrentFuncs.Clear();
        await Assert.That(ServiceInteraction.CanUseBank(_character)).IsFalse();
    }
}
