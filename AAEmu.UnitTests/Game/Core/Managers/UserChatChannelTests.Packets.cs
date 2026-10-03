using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.StaticValues;

using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class UserChatChannelTests
{
    [Test]
    public async Task Packets_JoinSendAndLeave_ConsumeEveryByteAndPreserveTheKey()
    {
        var owner = Character(1);
        var create = new PacketStream().Write("Travel").Write("secret").Write(true);
        new CSJoinUserChatChannelPacket { Connection = owner.Connection }.Read(create);
        await Assert.That(create.LeftBytes).IsEqualTo(0);
        var joined = Body(owner.Session.Packets.Single());
        var key = joined.ReadUInt64();
        await Assert.That(key & 0xffff).IsEqualTo(15UL);
        await Assert.That(joined.ReadString()).IsEqualTo("Travel");
        await Assert.That(joined.LeftBytes).IsEqualTo(0);

        var message = SendBody(key, "Message");
        new CSSendChatMessagePacket { Connection = owner.Connection }.Read(message);
        await Assert.That(message.LeftBytes).IsEqualTo(0);
        await Assert.That(Body(owner.Session.Packets.Last()).ReadUInt64()).IsEqualTo(key);

        var leave = new PacketStream().Write(key);
        new CSLeaveChatChannelPacket { Connection = owner.Connection }.Read(leave);
        await Assert.That(leave.LeftBytes).IsEqualTo(0);
        await Assert.That(Body(owner.Session.Packets.Last()).ReadUInt64()).IsEqualTo(key);
        await Assert.That(Channels.Send(owner.Connection, key, "removed", 0, 0)).IsFalse();
    }

    [Test]
    [Arguments("name")]
    [Arguments("password")]
    [Arguments("flag")]
    [Arguments("trailing")]
    [Arguments("utf8")]
    [Arguments("nul")]
    public async Task JoinPacket_InvalidBody_HasNoMembershipSideEffect(string mismatch)
    {
        var owner = Character(1);
        var packet = new PacketStream();
        if (mismatch == "utf8")
            packet.Write((ushort)2).Write((byte)0xc3).Write((byte)0x28);
        else
            packet.Write(mismatch == "name" ? new string('a', 49) : mismatch == "nul" ? "A\0B" : "Travel");
        packet.Write(mismatch == "password" ? "1234567" : "");
        packet.Write(mismatch == "flag" ? (byte)2 : (byte)1);
        if (mismatch == "trailing")
            packet.Write((byte)0);

        await Assert.That(() => new CSJoinUserChatChannelPacket { Connection = owner.Connection }.Read(packet))
            .Throws<InvalidDataException>();
        await Assert.That(owner.Session.Packets).IsEmpty();
        await Assert.That(Channels.Join(owner.Connection, "Travel", "", true) != 0).IsTrue();
    }

    [Test]
    public async Task SendPacket_MissingNativeTail_RejectsBeforeDispatch()
    {
        var owner = Character(1);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        owner.Session.Packets.Clear();
        var packet = new PacketStream().Write(key).Write("").Write("Message").Write((byte)0).Write(0);

        await Assert.That(() => new CSSendChatMessagePacket { Connection = owner.Connection }.Read(packet))
            .Throws<MarshalException>();
        await Assert.That(owner.Session.Packets).IsEmpty();
    }

    [Test]
    public async Task LeavePacket_TrailingByte_DoesNotRemoveMembership()
    {
        var owner = Character(1);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        var packet = new PacketStream().Write(key).Write((byte)0);

        await Assert.That(() => new CSLeaveChatChannelPacket { Connection = owner.Connection }.Read(packet))
            .Throws<InvalidDataException>();
        await Assert.That(Channels.Send(owner.Connection, key, "still joined", 0, 0)).IsTrue();
    }

    [Test]
    public async Task SendPacket_MutedAccount_DoesNotReachChannelMembers()
    {
        var owner = Character(1);
        var guest = Character(2);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        Channels.Join(guest.Connection, "Travel", "", false);
        _moderation.ApplyState(new ModerationState(owner.AccountId, 2, false, 0, true, 0));
        guest.Session.Packets.Clear();

        new CSSendChatMessagePacket { Connection = owner.Connection }.Read(SendBody(key, "Muted message"));

        await Assert.That(guest.Session.Packets).IsEmpty();
    }

    [Test]
    public async Task SendPacket_SpamRule_DoesNotBypassTheCurrentAccountLimit()
    {
        var owner = Character(1);
        var guest = Character(2);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        Channels.Join(guest.Connection, "Travel", "", false);
        guest.Session.Packets.Clear();
        SetInstance(new ChatSpamManager(Mock.Of<ISusManager>().Object, TimeProvider.System,
            Options.Create(new AppConfiguration
            {
                ChatSpam = new ChatSpamConfig { Enabled = true, RateMessageCount = 2, RepeatMessageCount = 0 }
            }), new ChatSpamGameData()));

        new CSSendChatMessagePacket { Connection = owner.Connection }.Read(SendBody(key, "First message"));
        new CSSendChatMessagePacket { Connection = owner.Connection }.Read(SendBody(key, "Second message"));

        await Assert.That(guest.Session.Packets.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SendPacket_LevelOne_KeepsTheCurrentUserChannelMinimum()
    {
        var owner = Character(1);
        owner.Level = 1;
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        owner.Session.Packets.Clear();

        new CSSendChatMessagePacket { Connection = owner.Connection }.Read(SendBody(key, "Hello"));

        await Assert.That(owner.Session.Packets.Count).IsEqualTo(1);
        await Assert.That(Body(owner.Session.Packets[0]).ReadUInt64()).IsEqualTo(key);
    }

    [Test]
    public async Task OutgoingPackets_BuiltinConstructors_KeepTheirOriginalFields()
    {
        var joined = new SCJoinedChatChannelPacket(ChatType.Ally, -1, (FactionsEnum)0x12345678).Write(new PacketStream());
        await Assert.That(joined.ReadInt16()).IsEqualTo((short)ChatType.Ally);
        await Assert.That(joined.ReadInt16()).IsEqualTo((short)-1);
        await Assert.That(joined.ReadUInt32()).IsEqualTo(0x12345678u);
        await Assert.That(joined.ReadString()).IsEqualTo("");
        await Assert.That(joined.LeftBytes).IsEqualTo(0);
        var left = new SCLeavedChatChannelPacket(ChatType.Ally, -1, (FactionsEnum)0x12345678).Write(new PacketStream());
        await Assert.That(left.ReadInt16()).IsEqualTo((short)ChatType.Ally);
        await Assert.That(left.ReadInt16()).IsEqualTo((short)-1);
        await Assert.That(left.ReadUInt32()).IsEqualTo(0x12345678u);
        await Assert.That(left.LeftBytes).IsEqualTo(0);
        var owner = Character(1);
        var message = new SCChatMessagePacket(ChatType.Ally, owner, "Hello", 3, 2).Write(new PacketStream());
        await Assert.That(message.ReadInt16()).IsEqualTo((short)ChatType.Ally);
        await Assert.That(message.ReadInt16()).IsEqualTo((short)owner.Faction.Id);
        await Assert.That(message.ReadUInt32()).IsEqualTo((uint)owner.Faction.Id);
        message.ReadBc();
        message.ReadUInt32();
        await Assert.That(message.ReadByte()).IsEqualTo((byte)0);
    }

    private static PacketStream SendBody(ulong key, string message)
    {
        var packet = new PacketStream().Write(key).Write("").Write(message).Write((byte)0).Write(0);
        for (var index = 0; index < 4; index++)
            packet.Write((ushort)(index + 1)).Write((ushort)(index + 2));
        return packet;
    }
}
