using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class ExpeditionAuthorizationTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private WorldManager _world;
    private ChatManager _chat;
    private ExpeditionManager _manager;
    private Mock<IExpeditionIdManager> _ids;
    private Expedition _guild;
    private RecordingCharacter _owner;
    private RecordingCharacter _officer;
    private RecordingCharacter _member;
    private RecordingCharacter _outsider;
    private Dictionary<FactionsEnum, Expedition> _guilds;
    private int _saves;

    [Before(Test)]
    public void SetUp()
    {
        _world = new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        _chat = new ChatManager();
        SetInstance(_world);
        SetInstance(_chat);
        var factions = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        SetField(factions, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>
        {
            [(FactionsEnum)101] = new() { Id = (FactionsEnum)101, MotherId = FactionsEnum.NuiaAlliance }
        });
        SetInstance(factions);
        _ids = Mock.Of<IExpeditionIdManager>();
        _manager = new ExpeditionManager(_ids.Object, Mock.Of<ITeamManager>().Object, _world, _chat)
        {
            PersistExpedition = _ => _saves++
        };
        SetInstance(_manager);
        SetField(_manager, "_nameRegex", new Regex("^[a-zA-Zа-яА-Я ]{3,32}$"));
        _owner = MakeCharacter(1);
        _officer = MakeCharacter(2);
        _member = MakeCharacter(3);
        _outsider = MakeCharacter(4);
        _guild = new Expedition { Id = (FactionsEnum)1000, MotherId = (FactionsEnum)101,
            Name = "Guild", OwnerId = _owner.Id, OwnerName = _owner.Name };
        foreach (var (character, role) in new[] { (_owner, (byte)255), (_officer, (byte)3), (_member, (byte)0) })
        {
            character.Expedition = _guild;
            _guild.Members.Add(ExpeditionManager.GetMemberFromCharacter(_guild, character, role == 255));
            _guild.Members[^1].Role = role;
            _guild.Policies.Add(new ExpeditionRolePolicy { ExpeditionId = _guild.Id, Role = role, Name = "Role",
                Invite = true, Expel = true, Promote = true, Chat = true });
        }
        _guilds = new() { [_guild.Id] = _guild };
        SetField(_manager, "_expeditions", _guilds);
        var channels = (ConcurrentDictionary<FactionsEnum, ChatChannel>)typeof(ChatManager)
            .GetProperty("GuildChannels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_chat)!;
        channels[_guild.Id] = new ChatChannel { ChatType = ChatType.Clan, InternalName = _guild.Name,
            Members = [_owner, _officer, _member] };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments("same_alliance")]
    [Arguments("different_roots")]
    [Arguments("null_faction")]
    public async Task Creation_UsesAllianceForEveryMemberBeforeChargingMoney(string reason)
    {
        var previous = AppConfiguration.Instance.Expedition;
        try
        {
            AppConfiguration.Instance.Expedition = new ExpeditionConfig
            {
                Create = new ExpeditionConfigCreate { Cost = 1, Level = 1, PartyMemberCount = 2 }
            };
            _owner.Expedition = null;
            _member.Expedition = null;
            if (reason == "different_roots")
            {
                _owner.Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance };
                _member.Faction = new SystemFaction { Id = FactionsEnum.Pirate };
            }
            if (reason == "null_faction") _member.Faction = null;
            var team = Mock.Of<ITeamManager>();
            team.GetActiveTeamByUnit(_owner.Id).Returns(new Team { Members = [new(_owner), new(_member)] });
            var manager = new ExpeditionManager(_ids.Object, team.Object, _world, _chat);
            SetField(manager, "_nameRegex", new Regex("^[a-zA-Z ]{3,32}$"));
            manager.CreateExpedition("New Guild", _owner.Connection);
            var body = new PacketStream(_owner.Session.Packets.Single()[8..]);
            await Assert.That(body.ReadInt16()).IsEqualTo((short)(reason == "same_alliance"
                ? ErrorMessageType.ExpeditionCreateMoney : ErrorMessageType.ExpeditionCreateFaction));
            await Assert.That(_owner.Expedition).IsNull();
        }
        finally
        {
            AppConfiguration.Instance.Expedition = previous;
        }
    }

    [Test]
    [Arguments("kick_owner")]
    [Arguments("kick_peer")]
    [Arguments("demote_owner")]
    [Arguments("demote_peer")]
    [Arguments("unknown_role")]
    [Arguments("promote_self")]
    public async Task MemberManagement_InvalidHierarchy_DoesNotChangeMembership(string action)
    {
        var target = action.EndsWith("owner", StringComparison.Ordinal) ? _owner : _officer;
        if (action.EndsWith("peer", StringComparison.Ordinal))
        {
            _guild.GetMember(_member).Role = 3;
            target = _member;
        }
        if (action.StartsWith("kick", StringComparison.Ordinal))
            _manager.Kick(_officer.Connection, target.Id);
        else
            _manager.ChangeMemberRole(_officer.Connection, action == "unknown_role" ? (byte)2 : (byte)0, target.Id);
        await Assert.That(_guild.Members.Count).IsEqualTo(3);
        await Assert.That(_guild.GetMember(_owner).Role).IsEqualTo((byte)255);
        await Assert.That(_guild.GetMember(_officer).Role).IsEqualTo((byte)3);
        await Assert.That(_saves).IsEqualTo(0);
    }

    [Test]
    public async Task MemberManagement_LowerRank_ChangesAndSaves()
    {
        _manager.ChangeMemberRole(_owner.Connection, 3, _member.Id);
        await Assert.That(_guild.GetMember(_member).Role).IsEqualTo((byte)3);
        _manager.Kick(_owner.Connection, _member.Id);
        await Assert.That(_member.Expedition).IsNull();
        await Assert.That(_guild.GetMember(_member)).IsNull();
        await Assert.That(_saves).IsEqualTo(2);
    }

    [Test]
    public async Task Leave_OwnerMustTransferFirst()
    {
        _manager.Leave(_owner);
        await Assert.That(_owner.Expedition).IsEqualTo(_guild);
        await Assert.That(_saves).IsEqualTo(0);
        _manager.ChangeOwner(_owner.Connection, _officer.Id);
        await Assert.That(_guild.OwnerId).IsEqualTo(_officer.Id);
        await Assert.That(_guild.OwnerName).IsEqualTo(_officer.Name);
        _manager.Leave(_owner);
        await Assert.That(_owner.Expedition).IsNull();
        await Assert.That(_guild.Members.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("officer")]
    [Arguments("outsider")]
    [Arguments("unknown_guild")]
    [Arguments("unknown_role")]
    public async Task PolicyEdit_UnauthorizedTarget_DoesNotChangePolicy(string reason)
    {
        var policy = new ExpeditionRolePolicy { ExpeditionId = reason == "unknown_guild" ? (FactionsEnum)9999 : _guild.Id,
            Role = reason == "unknown_role" ? (byte)5 : (byte)0, Name = "Changed" };
        var actor = reason == "officer" ? _officer : reason == "outsider" ? _outsider : _owner;
        _manager.ChangeExpeditionRolePolicy(actor.Connection, policy);
        await Assert.That(_guild.GetPolicyByRole(0).Name).IsEqualTo("Role");
        await Assert.That(_saves).IsEqualTo(0);
    }

    [Test]
    public async Task PolicyEdit_Owner_CopiesEveryFlagAndEnforcesChat()
    {
        var policy = new ExpeditionRolePolicy { ExpeditionId = _guild.Id, Role = 0, Name = "Changed",
            DominionDeclare = true, Invite = false, Expel = false, Promote = false, Dismiss = true,
            Chat = false, ManagerChat = true, SiegeMaster = true, JoinSiege = true };
        _manager.ChangeExpeditionRolePolicy(_owner.Connection, policy);
        var body = _guild.GetPolicyByRole(0).Write(new PacketStream()).GetBytes();
        await Assert.That(body.SequenceEqual(policy.Write(new PacketStream()).GetBytes())).IsTrue();
        await Assert.That(_guild.CanChat(_member)).IsFalse();
        await Assert.That(_guild.CanChat(_outsider)).IsFalse();
        await Assert.That(_guild.CanChat(_owner)).IsTrue();
        await Assert.That(_saves).IsEqualTo(1);
    }

    [Test]
    [Arguments("cross_faction")]
    [Arguments("pirate")]
    [Arguments("full")]
    public async Task Invitation_InvalidAllianceOrCapacity_DoesNotSend(string reason)
    {
        if (reason != "full")
            _outsider.Faction = new SystemFaction { Id = reason == "pirate" ? (FactionsEnum)114 : FactionsEnum.HaranyaAlliance };
        else
            FillGuild(ExpeditionManager.MemberLimit);
        _manager.Invite(_owner.Connection, _outsider.Name);
        await Assert.That(_outsider.Session.Packets.Count).IsEqualTo(0);
        _manager.ReplyInvite(_outsider.Connection, _guild.Id, _owner.Id, true);
        await Assert.That(_outsider.Expedition).IsNull();
    }

    [Test]
    [Arguments("cross_faction")]
    [Arguments("full")]
    [Arguments("disbanded")]
    [Arguments("expired")]
    [Arguments("uninvited")]
    [Arguments("reconnected_inviter")]
    [Arguments("reconnected_receiver")]
    public async Task Invitation_ReplyRechecksCurrentState(string reason)
    {
        var now = DateTime.UtcNow;
        _manager.InvitationTime = () => now;
        if (reason != "uninvited") _manager.Invite(_owner.Connection, _outsider.Name);
        if (reason == "cross_faction") _outsider.Faction = new SystemFaction { Id = FactionsEnum.HaranyaAlliance };
        if (reason == "full") FillGuild(ExpeditionManager.MemberLimit);
        if (reason == "disbanded") _manager.Disband(_owner);
        if (reason == "expired") now = now.AddMinutes(1);
        if (reason == "reconnected_inviter") _owner.Connection = new GameConnection(_owner.Session) { ActiveChar = _owner };
        if (reason == "reconnected_receiver") _outsider.Connection = new GameConnection(_outsider.Session) { ActiveChar = _outsider };
        _manager.ReplyInvite(_outsider.Connection, _guild.Id, _owner.Id, true);
        await Assert.That(_outsider.Expedition).IsNull();
        await Assert.That(_guild.GetMember(_outsider)).IsNull();
    }

    [Test]
    public async Task Invitation_LastPlace_OnlyOnePendingInviteCanJoin()
    {
        var second = MakeCharacter(5);
        FillGuild(ExpeditionManager.MemberLimit - 1);
        _manager.Invite(_owner.Connection, _outsider.Name);
        _manager.Invite(_owner.Connection, second.Name);
        await Task.WhenAll(Task.Run(() => _manager.ReplyInvite(_outsider.Connection, _guild.Id, _owner.Id, true)),
            Task.Run(() => _manager.ReplyInvite(second.Connection, _guild.Id, _owner.Id, true)));
        await Assert.That(_guild.Members.Count).IsEqualTo(ExpeditionManager.MemberLimit);
        await Assert.That(new[] { _outsider, second }.Count(character => character.Expedition == _guild)).IsEqualTo(1);
        await Assert.That(_saves).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OnCharacterLogin_StaleGuildPointer_ClearsMembershipAndTag(bool disbanded)
    {
        var character = disbanded ? _member : _outsider;
        character.Expedition = _guild;
        _guild.isDisbanded = disbanded;
        _guild.OnCharacterLogin(character);
        await Assert.That(character.Expedition).IsNull();
        await Assert.That(character.Broadcasts.OfType<SCUnitExpeditionChangedPacket>().Count()).IsEqualTo(1);
        await Assert.That(_guild.CanChat(character)).IsFalse();
    }

    [Test]
    public async Task OnCharacterLogin_ConcurrentKick_CannotRejoinTheGuildChannel()
    {
        using var started = new ManualResetEventSlim();
        Task login;
        bool loginWaited;
        lock (SaveManager.PersistenceSyncRoot)
        {
            login = Task.Run(() =>
            {
                started.Set();
                _guild.OnCharacterLogin(_member);
            });
            if (!started.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Guild login worker did not start.");
            loginWaited = !login.Wait(TimeSpan.FromMilliseconds(100));
            _manager.Kick(_owner.Connection, _member.Id);
        }
        await login;
        await Assert.That(loginWaited).IsTrue();
        await Assert.That(_member.Expedition).IsNull();
        await Assert.That(_chat.GetGuildChat(_guild).GetMembersSnapshot().Contains(_member)).IsFalse();
    }

    [Test]
    public async Task Disband_RemovesRegistryMembersPendingInvitesAndNearbyTags()
    {
        _manager.Invite(_owner.Connection, _outsider.Name);
        await Assert.That(_manager.Disband(_owner)).IsTrue();
        await Assert.That(_manager.GetExpedition(_guild.Id)).IsNull();
        await Assert.That(_manager.Expeditions.Any()).IsFalse();
        await Assert.That(_guild.Members.Count).IsEqualTo(0);
        foreach (var character in new[] { _owner, _officer, _member })
        {
            await Assert.That(character.Expedition).IsNull();
            var changed = character.Broadcasts.OfType<SCUnitExpeditionChangedPacket>().Single();
            var body = new PacketStream(changed.Write(new PacketStream()).GetBytes());
            await Assert.That(body.ReadBc()).IsEqualTo(character.ObjId);
            await Assert.That(body.ReadUInt32()).IsEqualTo(character.Id);
            await Assert.That(body.ReadString()).IsEqualTo("");
            await Assert.That(body.ReadString()).IsEqualTo(character.Name);
            await Assert.That(body.ReadUInt32()).IsEqualTo((uint)_guild.Id);
            await Assert.That(body.ReadUInt32()).IsEqualTo(0u);
            await Assert.That(body.ReadBoolean()).IsFalse();
            await Assert.That(body.LeftBytes).IsEqualTo(0);
        }
        _manager.ReplyInvite(_outsider.Connection, _guild.Id, _owner.Id, true);
        await Assert.That(_outsider.Expedition).IsNull();
        _ids.ReleaseId((uint)_guild.Id).WasCalled(Times.Once);
    }

    [Test]
    [Arguments("officer")]
    [Arguments("wrong_id")]
    [Arguments("nation")]
    [Arguments("empty")]
    [Arguments("invalid")]
    [Arguments("too_long")]
    [Arguments("duplicate")]
    [Arguments("newline")]
    public async Task Rename_InvalidOwnerOrName_DoesNotSave(string reason)
    {
        _guilds[(FactionsEnum)1001] = new Expedition { Id = (FactionsEnum)1001, Name = "Other Guild" };
        var name = reason switch { "empty" => "", "invalid" => "Invalid123", "too_long" => new string('A', 33),
            "duplicate" => "other guild", "newline" => "Guild\n", _ => "New Guild" };
        var actor = reason == "officer" ? _officer : _owner;
        await Assert.That(_manager.Rename(actor, reason == "wrong_id" ? (FactionsEnum)9999 : _guild.Id,
            name, reason != "nation")).IsFalse();
        await Assert.That(_guild.Name).IsEqualTo("Guild");
        await Assert.That(_saves).IsEqualTo(0);
    }

    [Test]
    [Arguments(3, false)]
    [Arguments(32, false)]
    [Arguments(32, true)]
    public async Task Rename_NameBoundaries_AcceptsTheConfiguredCharacters(int length, bool cyrillic)
    {
        var name = new string(cyrillic ? 'Я' : 'A', length);
        await Assert.That(_manager.Rename(_owner, _guild.Id, name, true)).IsTrue();
        await Assert.That(_guild.Name).IsEqualTo(name);
        await Assert.That(_saves).IsEqualTo(1);
    }

    [Test]
    public async Task Rename_ValidPacket_SavesAndBroadcastsExactBody()
    {
        var stream = new PacketStream();
        stream.Write((uint)_guild.Id); stream.Write("New Guild"); stream.Write(true); stream.Rollback();
        new CSRenameExpeditionPacket { Connection = _owner.Connection }.Read(stream);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
        await Assert.That(_guild.Name).IsEqualTo("New Guild");
        await Assert.That(_saves).IsEqualTo(1);
        var body = new PacketStream(new SCFactionRenamedPacket((uint)_guild.Id, _guild.Name, false)
            .Write(new PacketStream()).GetBytes());
        await Assert.That(body.ReadUInt32()).IsEqualTo((uint)_guild.Id);
        await Assert.That(body.ReadString()).IsEqualTo("New Guild");
        await Assert.That(body.ReadBoolean()).IsFalse();
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Rename_TruncatedOrTrailingPacket_DoesNotChangeName()
    {
        var stream = new PacketStream();
        stream.Write((uint)_guild.Id); stream.Write("New Guild"); stream.Write(true);
        var bytes = stream.GetBytes();
        for (var length = 0; length < bytes.Length; length++)
            await Assert.That(() => new CSRenameExpeditionPacket { Connection = _owner.Connection }
                .Read(new PacketStream(bytes[..length]))).ThrowsException();
        stream.Write((byte)1); stream.Rollback();
        await Assert.That(() => new CSRenameExpeditionPacket { Connection = _owner.Connection }.Read(stream))
            .Throws<InvalidDataException>();
        await Assert.That(_guild.Name).IsEqualTo("Guild");
        await Assert.That(_saves).IsEqualTo(0);
    }

    [Test]
    [Arguments(129, 1)]
    [Arguments(65535, 1)]
    [Arguments(4, 2)]
    public async Task Rename_InvalidNativeLengthOrFlag_DoesNotChangeName(int length, int flag)
    {
        var stream = new PacketStream();
        stream.Write((uint)_guild.Id); stream.Write((ushort)length);
        stream.Write(new byte[] { 71, 117, 105, 108 }); stream.Write((byte)flag); stream.Rollback();
        await Assert.That(() => new CSRenameExpeditionPacket { Connection = _owner.Connection }.Read(stream))
            .Throws<InvalidDataException>();
        await Assert.That(_guild.Name).IsEqualTo("Guild");
        await Assert.That(_saves).IsEqualTo(0);
    }

    [Test]
    public async Task Invitation_Acceptance_HoldsCharacterSaveLockThroughStatePublication()
    {
        _manager.Invite(_owner.Connection, _outsider.Name);
        var lockHeld = false;
        var statePublishedUnderLock = false;
        _outsider.OnBroadcast = _ => statePublishedUnderLock = Monitor.IsEntered(SaveManager.PersistenceSyncRoot) &&
            ReferenceEquals(_outsider.Expedition, _guild);
        _manager.PersistExpedition = _ => lockHeld = Monitor.IsEntered(SaveManager.PersistenceSyncRoot);
        _manager.ReplyInvite(_outsider.Connection, _guild.Id, _owner.Id, true);
        await Assert.That(lockHeld).IsTrue();
        await Assert.That(statePublishedUnderLock).IsTrue();
        await Assert.That(_outsider.Expedition).IsEqualTo(_guild);
        await Assert.That(_outsider.Broadcasts.OfType<SCUnitExpeditionChangedPacket>().Any()).IsTrue();
    }

    [Test]
    [Arguments("kick")]
    [Arguments("leave")]
    [Arguments("role")]
    [Arguments("owner")]
    [Arguments("policy")]
    [Arguments("rename")]
    [Arguments("disband")]
    [Arguments("join")]
    public async Task Mutation_SaveFails_RestoresStateBeforeAnySuccessPacket(string action)
    {
        if (action == "join") _manager.Invite(_owner.Connection, _outsider.Name);
        foreach (var character in new[] { _owner, _officer, _member, _outsider })
            character.Session.Packets.Clear();
        _manager.PersistExpedition = _ => throw new IOException("Injected guild save failure");
        await Assert.That(() =>
        {
            switch (action)
            {
                case "kick": _manager.Kick(_owner.Connection, _member.Id); break;
                case "leave": _manager.Leave(_member); break;
                case "role": _manager.ChangeMemberRole(_owner.Connection, 3, _member.Id); break;
                case "owner": _manager.ChangeOwner(_owner.Connection, _member.Id); break;
                case "policy": _manager.ChangeExpeditionRolePolicy(_owner.Connection,
                    new ExpeditionRolePolicy { ExpeditionId = _guild.Id, Role = 0, Name = "Changed" }); break;
                case "rename": _manager.Rename(_owner, _guild.Id, "New Guild", true); break;
                case "disband": _manager.Disband(_owner); break;
                case "join": _manager.ReplyInvite(_outsider.Connection, _guild.Id, _owner.Id, true); break;
            }
        }).Throws<IOException>();
        await Assert.That(_guild.Members.Count).IsEqualTo(3);
        await Assert.That(_guild.OwnerId).IsEqualTo(_owner.Id);
        await Assert.That(_guild.GetMember(_owner).Role).IsEqualTo((byte)255);
        await Assert.That(_guild.GetMember(_member).Role).IsEqualTo((byte)0);
        await Assert.That(_guild.GetPolicyByRole(0).Name).IsEqualTo("Role");
        await Assert.That(_guild.Name).IsEqualTo("Guild");
        await Assert.That(_guild.isDisbanded).IsFalse();
        await Assert.That(_member.Expedition).IsEqualTo(_guild);
        await Assert.That(_outsider.Expedition).IsNull();
        foreach (var character in new[] { _owner, _officer, _member, _outsider })
        {
            await Assert.That(character.Session.Packets.Count).IsEqualTo(0);
            await Assert.That(character.Broadcasts.Count).IsEqualTo(0);
        }
        _ids.ReleaseId((uint)_guild.Id).WasCalled(Times.Never);
    }

    private void FillGuild(int count)
    {
        while (_guild.Members.Count < count)
            _guild.Members.Add(new ExpeditionMember { CharacterId = (uint)(10000 + _guild.Members.Count),
                ExpeditionId = _guild.Id, Role = 0, Name = "Offline" });
    }

    private RecordingCharacter MakeCharacter(uint id)
    {
        var character = new RecordingCharacter { Id = id, ObjId = id + 100, Name = $"Member{id}", Level = 30,
            Faction = new SystemFaction { Id = (FactionsEnum)102, MotherId = FactionsEnum.NuiaAlliance } };
        character.Connection = new GameConnection(character.Session) { ActiveChar = character };
        character.Blocked = new CharacterBlocked(character);
        SetField(character, "<IsOnline>k__BackingField", true, typeof(Character));
        SetField(character, "<Achievements>k__BackingField", new CharacterAchievements(character, new AchievementGameData()), typeof(Character));
        _world.TryAddCharacter(character);
        return character;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object instance, string name, object value, Type type = null) =>
        (type ?? instance.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class RecordingCharacter : CharacterMock
    {
        public RecordingSession Session { get; } = new();
        public List<GamePacket> Broadcasts { get; } = [];
        public Action<GamePacket> OnBroadcast { get; set; }
        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            Broadcasts.Add(packet);
            OnBroadcast?.Invoke(packet);
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
