using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed partial class UserChatChannelTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private readonly ChatManager _chat = new();
    private readonly ModerationManager _moderation = new(null, _ => false, () => [], (_, _) => { },
        TimeProvider.System, TimeSpan.FromSeconds(1));
    private UserChatChannels Channels => _chat.UserChannels;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(_chat);
        SetInstance(_moderation);
        SetInstance(new ChatSpamManager(Mock.Of<ISusManager>().Object, TimeProvider.System,
            Options.Create(new AppConfiguration { ChatSpam = new ChatSpamConfig { Enabled = false } }), new ChatSpamGameData()));
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE allowed_name_chars (id INTEGER, char TEXT, bytes INTEGER);
            CREATE TABLE blocked_texts
                (id INTEGER, utf8str TEXT, bytes INTEGER, check_name TEXT, check_chat TEXT, partial_match TEXT);
            INSERT INTO blocked_texts VALUES (1, 'Admin', 5, 't', 'f', 't');
            """;
        command.ExecuteNonQuery();
        var data = new NameGameData();
        data.Load(connection);
        SetInstance(data);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    public async Task JoinAndSend_TwoChannels_UseDistinctFullKeysAndAudience()
    {
        var first = Character(1);
        var second = Character(2);
        var third = Character(3);
        var a = Channels.Join(first.Connection, "Travel", "", true);
        var b = Channels.Join(first.Connection, "Trade", "", true);
        await Assert.That(Channels.Join(second.Connection, "tRaVeL", "", false)).IsEqualTo(a);
        await Assert.That(Channels.Join(third.Connection, "Trade", "", false)).IsEqualTo(b);
        await Assert.That(a != b && (ushort)a == 15 && (ushort)b == 15).IsTrue();
        first.Session.Packets.Clear();
        second.Session.Packets.Clear();
        third.Session.Packets.Clear();

        await Assert.That(Channels.Send(first.Connection, a, "hello", 17, 2)).IsTrue();

        await Assert.That(first.Session.Packets.Count).IsEqualTo(1);
        await Assert.That(second.Session.Packets.Count).IsEqualTo(1);
        await Assert.That(third.Session.Packets).IsEmpty();
        var body = Body(second.Session.Packets.Single());
        await Assert.That(body.ReadUInt64()).IsEqualTo(a);
        await Assert.That(body.ReadBc()).IsEqualTo(first.ObjId);
        await Assert.That(body.ReadUInt32()).IsEqualTo(first.Id);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)2);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)first.Race);
        await Assert.That(body.ReadUInt32()).IsEqualTo((uint)first.Faction.Id);
        await Assert.That(body.ReadString()).IsEqualTo(first.Name);
        await Assert.That(body.ReadString()).IsEqualTo("hello");
        await Assert.That(body.ReadInt32()).IsEqualTo(17);
        await Assert.That(body.ReadInt32()).IsEqualTo(0);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments("", ErrorMessageType.ChatPrivateChannel)]
    [Arguments("Secret", ErrorMessageType.ChatWrongPassword)]
    public async Task Join_PasswordMismatch_DoesNotAddMembership(string password, ErrorMessageType error)
    {
        var owner = Character(1);
        var guest = Character(2);
        var key = Channels.Join(owner.Connection, "Private", "secret", true);
        await Assert.That(Channels.Join(guest.Connection, "Private", password, false)).IsEqualTo(0UL);
        await Assert.That(Body(guest.Session.Packets.Single()).ReadInt16()).IsEqualTo((short)error);
        await Assert.That(Channels.Send(guest.Connection, key, "forged", 0, 0)).IsFalse();
        await Assert.That(Channels.Join(guest.Connection, "Private", "secret", false)).IsEqualTo(key);
    }

    [Test]
    public async Task Join_DuplicateAndMissingChannels_ReturnTheConfirmedErrors()
    {
        var owner = Character(1);
        Channels.Join(owner.Connection, "Travel", "", true);
        owner.Session.Packets.Clear();
        await Assert.That(Channels.Join(owner.Connection, "TRAVEL", "", true)).IsEqualTo(0UL);
        await Assert.That(Body(owner.Session.Packets.Last()).ReadInt16()).IsEqualTo((short)ErrorMessageType.ChatChannelAlreadyExists);
        await Assert.That(Channels.Join(owner.Connection, "TRAVEL", "", false)).IsEqualTo(0UL);
        await Assert.That(Body(owner.Session.Packets.Last()).ReadInt16()).IsEqualTo((short)ErrorMessageType.ChatAlreadyJoinedChannel);
        await Assert.That(Channels.Join(owner.Connection, "Missing", "", false)).IsEqualTo(0UL);
        await Assert.That(Body(owner.Session.Packets.Last()).ReadInt16()).IsEqualTo((short)ErrorMessageType.ChatNoChannel);
    }

    [Test]
    public async Task Join_ConcurrentCaseEquivalentCreates_OnlyCreatesOneChannel()
    {
        var first = Character(1);
        var second = Character(2);
        var results = await Task.WhenAll(
            Task.Run(() => Channels.Join(first.Connection, "Travel", "", true)),
            Task.Run(() => Channels.Join(second.Connection, "TRAVEL", "", true)));

        await Assert.That(results.Count(key => key != 0)).IsEqualTo(1);
        var other = results[0] == 0 ? first : second;
        await Assert.That(Channels.Join(other.Connection, "travel", "", false)).IsEqualTo(results.Max());
    }

    [Test]
    public async Task Join_ConcurrentRequests_EnforcesFiveMembershipsWithoutOrphanChannels()
    {
        var owner = Character(1);
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(index => Task.Run(() => Channels.Join(owner.Connection, $"Channel{index}", "", true))));
        await Assert.That(results.Count(key => key != 0)).IsEqualTo(5);
        var failed = Array.IndexOf(results, 0UL);
        await Assert.That(Channels.Join(Character(2).Connection, $"Channel{failed}", "", false)).IsEqualTo(0UL);
        Channels.Leave(owner.Connection, results.First(key => key != 0));
        await Assert.That(Channels.Join(owner.Connection, $"Channel{failed}", "", true) != 0).IsTrue();
    }

    [Test]
    public async Task LeaveAndDisconnect_DeletesEmptyChannelsAndNeverReusesKeys()
    {
        var owner = Character(1);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        await Assert.That(Channels.Leave(owner.Connection, key)).IsTrue();
        var left = Body(owner.Session.Packets.Last());
        await Assert.That(left.ReadUInt64()).IsEqualTo(key);
        await Assert.That(left.LeftBytes).IsEqualTo(0);
        var replacement = Channels.Join(owner.Connection, "Travel", "", true);
        await Assert.That(replacement != key).IsTrue();
        await Assert.That(Channels.Send(owner.Connection, key, "stale", 0, 0)).IsFalse();
        await Assert.That(Channels.Leave(owner.Connection, key)).IsFalse();
        _chat.LeaveAllChannels(owner);
        await Assert.That(Channels.Join(Character(2).Connection, "Travel", "", false)).IsEqualTo(0UL);
    }

    [Test]
    public async Task SendOrLeave_ForgedKeyOrNonMember_DoesNotReachMembers()
    {
        var owner = Character(1);
        var outsider = Character(2);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        owner.Session.Packets.Clear();
        await Assert.That(Channels.Send(outsider.Connection, key, "forged", 0, 0)).IsFalse();
        await Assert.That(Channels.Leave(outsider.Connection, key)).IsFalse();
        await Assert.That(Channels.Send(owner.Connection, key ^ (1UL << 48), "forged", 0, 0)).IsFalse();
        await Assert.That(owner.Session.Packets.Count).IsEqualTo(1);
        await Assert.That(Body(owner.Session.Packets[0]).ReadInt16()).IsEqualTo((short)ErrorMessageType.ChatNotJoinedChannel);
        await Assert.That(Channels.Send(owner.Connection, key, "valid", 0, 0)).IsTrue();
    }

    [Test]
    public async Task JoinAndSend_FactionBoundaries_UseCurrentAllegianceAndPruneChangedMembers()
    {
        var owner = Character(1);
        var elf = Character(2);
        elf.Faction.Id = (FactionsEnum)103;
        var pirate = Character(3, FactionsEnum.Pirate);
        var key = Channels.Join(owner.Connection, "Travel", "", true);
        await Assert.That(Channels.Join(pirate.Connection, "Travel", "", false)).IsEqualTo(0UL);
        var pirateKey = Channels.Join(pirate.Connection, "Travel", "", true);
        await Assert.That(pirateKey != 0 && pirateKey != key).IsTrue();
        await Assert.That(Channels.Join(elf.Connection, "Travel", "", false)).IsEqualTo(key);
        elf.Faction = new SystemFaction { Id = FactionsEnum.Pirate };
        owner.Session.Packets.Clear();
        elf.Session.Packets.Clear();

        await Assert.That(Channels.Send(owner.Connection, key, "friendly", 0, 0)).IsTrue();
        await Assert.That(elf.Session.Packets.Count).IsEqualTo(1);
        await Assert.That(Body(elf.Session.Packets[0]).ReadUInt64()).IsEqualTo(key);
        await Assert.That(Channels.Send(elf.Connection, key, "hostile", 0, 0)).IsFalse();
        await Assert.That(owner.Session.Packets.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LeaveAll_StaleCharacterReference_DoesNotRemoveTheReplacementSession()
    {
        var old = Character(1);
        Channels.Join(old.Connection, "Old", "", true);
        _chat.LeaveAllChannels(old);
        var current = Character(1);
        var key = Channels.Join(current.Connection, "New", "", true);

        _chat.LeaveAllChannels(old);

        await Assert.That(Channels.Send(current.Connection, key, "current", 0, 0)).IsTrue();
        await Assert.That(Channels.Send(old.Connection, key, "stale", 0, 0)).IsFalse();
    }

    [Test]
    [Arguments("", false)]
    [Arguments("A", true)]
    [Arguments("A B", true)]
    [Arguments(" A", false)]
    [Arguments("A\u00a0", false)]
    [Arguments("A|B", false)]
    [Arguments("A\nB", false)]
    [Arguments("The Admin", false)]
    [Arguments("😀", false)]
    public async Task NamePolicy_PrintableNames_RejectsInvalidText(string name, bool expected)
    {
        await Assert.That(UserChatChannels.ValidName(name)).IsEqualTo(expected);
    }

    [Test]
    public async Task NameAndPasswordLimits_CountUtf8BytesAndKeepExactPasswordSpaces()
    {
        await Assert.That(UserChatChannels.ValidName(new string('界', 16))).IsTrue();
        await Assert.That(UserChatChannels.ValidName(new string('界', 17))).IsFalse();
        await Assert.That(UserChatChannels.ValidPassword("界界")).IsTrue();
        await Assert.That(UserChatChannels.ValidPassword("界界a")).IsFalse();
        var owner = Character(1);
        var guest = Character(2);
        var key = Channels.Join(owner.Connection, "Travel", " a ", true);
        await Assert.That(Channels.Join(guest.Connection, "Travel", "a", false)).IsEqualTo(0UL);
        await Assert.That(Channels.Join(guest.Connection, "Travel", " a ", false)).IsEqualTo(key);
    }

    private RecordingCharacter Character(uint id, FactionsEnum alliance = FactionsEnum.NuiaAlliance)
    {
        var character = new RecordingCharacter { Id = id, ObjId = id + 100, AccountId = id, Name = $"Player{id}", Level = 30,
            Faction = new SystemFaction { Id = alliance, MotherId = alliance }, Race = Race.Nuian };
        character.Connection = new GameConnection(character.Session) { ActiveChar = character, AccountId = id };
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(character, true);
        _moderation.ApplyState(new ModerationState(id, 1, false, 0, false, 0));
        return character;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static PacketStream Body(byte[] packet) => new(packet[8..]);

    private sealed class RecordingCharacter : CharacterMock
    {
        internal RecordingSession Session { get; } = new();
        internal Action<GamePacket> OnBroadcast { get; set; }
        public override void BroadcastPacket(GamePacket packet, bool self) => OnBroadcast?.Invoke(packet);
    }

    private sealed class RecordingSession : ISession
    {
        public List<byte[]> Packets { get; } = [];
        internal Action OnSend { get; set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet)
        {
            OnSend?.Invoke();
            Packets.Add(packet.ToArray());
        }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
