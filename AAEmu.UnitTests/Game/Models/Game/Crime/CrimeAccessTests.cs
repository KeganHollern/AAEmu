using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Teleport;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;

using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.Crime;

[NotInParallel]
public sealed class CrimeAccessTests
{
    private readonly List<Action> _restore = [];
    private readonly List<WorldInstance> _worlds = [];
    private WorldManager _worldManager;
    private InstantGameManager _instantGames;
    private TrialManager _trials;

    [Before(Test)]
    public void SetUp()
    {
        var friends = new FriendMananger();
        SetField(friends, "_allFriends", new Dictionary<uint, FriendTemplate>());
        Install(friends);
        var skills = new SkillManager(null, null);
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>> { [(uint)BuffConstants.TagPrisoner] = [1] });
        Install(skills);
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        Install(models);
        _worldManager = new WorldManager(new TickManager(), Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(_worldManager);
        Install(new SusManager(_worldManager));
        _instantGames = new InstantGameManager();
        Install(_instantGames);
        _trials = new TrialManager();
        Install(_trials);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var world in _worlds)
            GC.SuppressFinalize(world);
        foreach (var restore in _restore)
            restore();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(-1)]
    public async Task Prisoner_InitialQueueAndDungeonRequest_DoNotAdmit(int pendingMinutes)
    {
        var player = Player(1);
        MakePrisoner(player, pendingMinutes == 0);
        player.OfflineGuiltyTime = pendingMinutes;
        _instantGames.ApplyToBattlefield(1, InstantCorps.Any, player);
        var manager = new IndunManager(new TickManager(), _worldManager, Mock.Of<IZoneManager>().Object,
            Mock.Of<ITeamManager>().Object, TimeProvider.System, Options.Create(new AppConfiguration()));

        await Assert.That(GetField<Dictionary<uint, List<MatchmakingApplicant>>>(_instantGames, "_matchmakingQueue")).IsEmpty();
        await Assert.That(manager.RequestDungeonInstance(player, 1, 0)).IsFalse();
        await Assert.That(manager.CreateSystemInstance(player, 1, 0)).IsNull();
    }

    [Test]
    [Arguments("invite")]
    [Arguments("acknowledge")]
    [Arguments("reset")]
    public async Task Prisoner_DelayedBattlefieldStep_DropsMembershipAndKeepsJailPosition(string step)
    {
        var player = Player(1);
        var game = Game(player, step == "reset");
        player.OfflineGuiltyTime = 1;
        player.Transform.Local.Position = new Vector3(10, 20, 30);
        player.Hp = 7;
        if (step == "invite")
            game.PlayerInviteResponse(player, true, 0);
        else if (step == "acknowledge")
            game.OnEnterWorld(player, 0);
        else
            game.ResetPlayers();

        await Assert.That(player.CurrentInstantGame).IsNull();
        await Assert.That(GetField<List<Character>>(game, "_players")).IsEmpty();
        await Assert.That(GetField<Dictionary<Character, InstantCorps>>(game, "_characterCorps")).IsEmpty();
        await Assert.That(player.Transform.World.Position).IsEqualTo(new Vector3(10, 20, 30));
        await Assert.That(player.Hp).IsEqualTo(7);
        await Assert.That(player.FactionChanges).IsEqualTo(step == "reset" ? 1 : 0);
        if (step == "reset")
            await Assert.That(player.LastFaction).IsEqualTo(FactionsEnum.NuiaAlliance);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResetAfterKill_PrisonerOrRemovedMembers_DoesNotResetEitherPlayer(bool prisoner)
    {
        var victim = Player(1);
        var killer = Player(2);
        var game = Game(victim, true);
        var members = GetField<Dictionary<Character, InstantGameTeamMember>>(game, "_members");
        var victimMember = members[victim];
        var killerMember = new InstantGameTeamMember { Character = killer };
        members.Add(killer, killerMember);
        killer.CurrentInstantGame = game;
        killer.OriginFaction = new SystemFaction { Id = FactionsEnum.NuiaAlliance };
        victim.Hp = 2;
        killer.Hp = 3;
        if (prisoner)
        {
            victim.OfflineGuiltyTime = 1;
            killer.OfflineGuiltyTime = 1;
        }
        else
        {
            victim.CurrentInstantGame = null;
            killer.CurrentInstantGame = null;
        }

        game.ResetAfterKill(killer, victim, killerMember, victimMember, InstantCorps.Corps1);

        await Assert.That(victim.Hp).IsEqualTo(2);
        await Assert.That(killer.Hp).IsEqualTo(3);
        await Assert.That(victim.CurrentInstantGame).IsNull();
        await Assert.That(killer.CurrentInstantGame).IsNull();
    }

    [Test]
    public async Task MoveToJusticeDestination_InstanceToMainZero_ClearsOldMembershipAndReturnPosition()
    {
        var main = World(0);
        var old = World(12);
        var player = Player(1);
        player.Transform.InstanceId = old.Id;
        old.AddObject(player);
        player.MainWorldPosition = player.Transform.CloneDetached(player);
        var game = Game(player, true);
        var queue = GetField<Dictionary<uint, List<MatchmakingApplicant>>>(_instantGames, "_matchmakingQueue");
        queue[1] = [new(player)];
        queue[2] = [new(player)];
        player.OfflineGuiltyTime = 1;
        var destination = new WorldSpawnPosition { WorldId = 1, X = 10, Y = 20, Z = 30, Yaw = 1.25f };

        var moved = PrisonerAccess.MoveToJusticeDestination(player, destination, TeleportReason.Lockup);
        game.OnEnterWorld(player, 0);
        game.LeaveInstantGame(player);

        await Assert.That(player.FactionChanges).IsEqualTo(1);
        await Assert.That(moved).IsTrue();
        await Assert.That(player.Transform.InstanceId).IsEqualTo(0u);
        await Assert.That(player.ParentWorld).IsSameReferenceAs(main);
        await Assert.That(main.HasCharacter(player.Id)).IsTrue();
        await Assert.That(old.HasCharacter(player.Id)).IsFalse();
        await Assert.That(player.MainWorldPosition).IsNull();
        await Assert.That(player.CurrentInstantGame).IsNull();
        await Assert.That(queue.Values.All(applicants => applicants.Count == 0)).IsTrue();
        await Assert.That(player.Transform.World.Position).IsEqualTo(new Vector3(10, 20, 30));
        await Assert.That(player.Transform.World.Rotation.Z).IsEqualTo(1.25f);
        await Assert.That(player.LastFaction).IsEqualTo(FactionsEnum.NuiaAlliance);
    }

    [Test]
    public async Task TrialChat_OnlyCurrentDefendantAndJurorsSpeak_AudienceOnlyReceives()
    {
        var trial = Trial();
        var juror = Player(2);
        trial.Jury[1] = new TrialJuryBox { JuryMember = juror, Seat = new Doodad() };
        var audience = Player(3);
        trial.CourtRoom.AudienceMembers.Add(audience);
        var outsider = Player(4);
        var session = Mock.Of<ISession>();
        outsider.Connection = new GameConnection(session.Object);
        trial.CourtRoom.TrialChatChannel.Members.Add(outsider);

        var recipients = _trials.GetTrialChatRecipients(trial, trial.Defendant);
        await Assert.That(recipients.Length).IsEqualTo(3);
        await Assert.That(recipients.Contains(outsider)).IsFalse();
        await Assert.That(_trials.GetTrialChatRecipients(trial, juror).Length).IsEqualTo(3);
        await Assert.That(_trials.SendTrialChat(audience, "denied", 0, 0)).IsEqualTo(0);
        await Assert.That(_trials.SendTrialChat(outsider, "denied", 0, 0)).IsEqualTo(0);
        var error = new SCErrorMsgPacket(ErrorMessageType.ChatNotInTrial, 0, true).Encode().GetBytes();
        session.SendPacket(Is<byte[]>(bytes => bytes.SequenceEqual(error))).WasCalled(Times.Once);
    }

    [Test]
    public async Task TrialChat_StaleAudienceAndTerminalTrial_DoNotReceiveMessages()
    {
        var trial = Trial();
        var audience = Player(3);
        trial.CourtRoom.AudienceMembers.Add(audience);
        audience.Transform.Local.Position = new Vector3(100, 0, 0);
        await Assert.That(_trials.GetTrialChatRecipients(trial, trial.Defendant).Length).IsEqualTo(1);
        audience.Transform.Local.Position = Vector3.Zero;
        trial.Step = TrialStep.EndTrial;
        await Assert.That(_trials.GetTrialChatRecipients(trial, trial.Defendant)).IsEmpty();
        await Assert.That(_trials.GetTrialAudienceSnapshot(trial).Single()).IsSameReferenceAs(audience);
        await Assert.That(_trials.GetParticipatingTrial(audience)).IsSameReferenceAs(trial);
        trial.CourtRoom.CurrentTrial = new TrialData();
        await Assert.That(_trials.GetTrialAudienceSnapshot(trial)).IsEmpty();
    }

    [Test]
    [Arguments("ordinary")]
    [Arguments("remote")]
    [Arguments("vertical")]
    [Arguments("self")]
    [Arguments("ownerless")]
    [Arguments("wrong_world")]
    [Arguments("unregistered")]
    [Arguments("dead")]
    [Arguments("prisoner")]
    [Arguments("offline")]
    [Arguments("invalid_kind")]
    [Arguments("zero_points")]
    [Arguments("wrong_skill")]
    [Arguments("wrong_function")]
    [Arguments("wrong_phase")]
    [Arguments("despawn")]
    public async Task ReportCrime_InvalidEvidenceOrReporter_DoesNotCreateHistory(string invalid)
    {
        var (reporter, evidence, function) = Evidence();
        var skillId = 11672u;
        var nextPhase = function.NextPhase;
        var functionId = function.FuncKey;
        switch (invalid)
        {
            case "ordinary": evidence.CurrentFuncs.Clear(); break;
            case "remote": reporter.Transform.Local.Position = new Vector3(5, 0, 0); break;
            case "vertical": reporter.Transform.Local.Position = new Vector3(0, 0, 5); break;
            case "self": evidence.OwnerId = reporter.Id; break;
            case "ownerless": evidence.OwnerType = DoodadOwnerType.System; break;
            case "wrong_world": evidence.ParentWorld = World(2); break;
            case "unregistered": evidence.ParentWorld.RemoveObject(evidence); break;
            case "dead": reporter.Hp = 0; break;
            case "prisoner": reporter.OfflineGuiltyTime = -1; break;
            case "offline": typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reporter, false); break;
            case "invalid_kind": SetEvidenceTemplate(0, 1); break;
            case "zero_points": SetEvidenceTemplate(3, 0); break;
            case "wrong_skill": skillId++; break;
            case "wrong_function": functionId++; break;
            case "wrong_phase": nextPhase++; break;
            case "despawn": evidence.Despawn = DateTime.UtcNow; break;
        }
        var manager = new CrimeManager();

        var result = manager.ReportCrime(reporter, evidence, skillId, nextPhase, functionId, "report");

        await Assert.That(result).IsNull();
        await Assert.That(manager.GetCrimesOfPlayer(evidence.OwnerId, true)).IsEmpty();
    }

    [Test]
    public async Task ReportCrime_AlteredEvidence_ConsumesPhaseWithoutInvalidHistory()
    {
        var (reporter, evidence, function) = Evidence();
        SetEvidenceTemplate(0, 0);
        var manager = new CrimeManager();

        var result = manager.ReportCrime(reporter, evidence, 11672, function.NextPhase, function.FuncKey, "report");

        await Assert.That(result).IsNull();
        await Assert.That(evidence.FuncGroupId).IsEqualTo(1043u);
        await Assert.That(manager.GetCrimesOfPlayer(99, true)).IsEmpty();
    }

    [Test]
    public async Task ReportCrime_ConcurrentValidReports_ConsumesCurrentPhaseOnce()
    {
        var (reporter, evidence, function) = Evidence();
        var criminal = Player(99);
        criminal.Hp = 10;
        reporter.ParentWorld.AddObject(criminal);
        var manager = new CrimeManager();
        var ids = new CrimeIdManager();
        typeof(IdManager).GetField("_freeIds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ids, new BitSet(32));
        var idField = typeof(CrimeIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldIds = idField.GetValue(null);
        _restore.Add(() => idField.SetValue(null, oldIds));
        idField.SetValue(null, ids);
        var zoneManager = new ZoneManager(_worldManager, Mock.Of<ITaskManager>().Object);
        SetField(zoneManager, "_zones", new Dictionary<uint, Zone>
            { [17] = new Zone { Id = 88, ZoneKey = 17 } });
        Install(zoneManager);
        evidence.Transform.ZoneId = 17;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            manager.ReportCrime(reporter, evidence, 11672, function.NextPhase, function.FuncKey, "report"))));

        await Assert.That(results.Count(result => result != null)).IsEqualTo(1);
        await Assert.That(results.Single(result => result != null).ZoneKey).IsEqualTo(17u);
        await Assert.That(manager.GetCrimesOfPlayer(99, true).Count).IsEqualTo(1);
        await Assert.That(criminal.CrimePoint).IsEqualTo((short)10);
        await Assert.That(evidence.FuncGroupId).IsEqualTo(1043u);
    }

    [Test]
    [Arguments(201, 201)]
    [Arguments(10, 2)]
    [Arguments(2, 10)]
    public async Task ReportPacket_InvalidTextLength_RejectsBeforeWorldLookup(int declared, int actual)
    {
        var stream = new PacketStream().WriteBc(1).Write(11672u).Write(1043).Write(1840u)
            .Write((ushort)declared).Write(new byte[actual], false);
        stream.Rollback();
        // No Connection is present. A malformed body must stop before character access.
        new CSReportCrimePacket().Read(stream);
        await Assert.That(stream.Pos).IsEqualTo(17);
    }

    private (Character Reporter, Doodad Evidence, DoodadFunc Function) Evidence()
    {
        var doodads = new DoodadManager(null, null, null, null, null);
        var function = new DoodadFunc { GroupId = 974, FuncId = 1, FuncKey = 1840, NextPhase = 1043,
            FuncType = nameof(DoodadFuncEvidenceItemLoot) };
        SetField(doodads, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>> { [974] = [function], [1043] = [] });
        SetField(doodads, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        Install(doodads);
        SetEvidenceTemplate(3, 10);
        GetField<Dictionary<uint, SkillTemplate>>(SkillManager.Instance, "_skills")[11672] =
            new SkillTemplate { Id = 11672, SourceAlive = true, MaxRange = 4, AllowToPrisoner = false };
        var world = World(1);
        var reporter = Player(1);
        reporter.Hp = 10;
        reporter.Transform.InstanceId = 1;
        var evidence = new Doodad { ObjId = 10, OwnerId = 99, OwnerType = DoodadOwnerType.Character,
            FuncGroupId = 974, Template = new DoodadTemplate() };
        evidence.Transform.InstanceId = 1;
        world.AddObject(evidence);
        return (reporter, evidence, function);
    }

    private static void SetEvidenceTemplate(uint kind, short points)
    {
        SetField(DoodadManager.Instance, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncEvidenceItemLoot)] = new()
            {
                [1] = new DoodadFuncEvidenceItemLoot { Id = 1, SkillId = 11672, CrimeKindId = kind, CrimeValue = points }
            }
        });
    }

    [Test]
    [Arguments(0)]
    [Arguments(100)]
    public async Task ReportPacket_NativeUtf8ByteLimit_ReadsCompleteBody(int characters)
    {
        var player = Player(1);
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = player };
        var stream = new PacketStream().WriteBc(1).Write(11672u).Write(1043).Write(1840u)
            .Write(new string('é', characters));
        stream.Rollback();

        new CSReportCrimePacket { Connection = connection }.Read(stream);

        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task TrialChat_RemovedOrOfflineJuror_CannotSpeakOrReceive()
    {
        var trial = Trial();
        var juror = Player(2);
        trial.Jury[1] = new TrialJuryBox { JuryMember = juror, Seat = new Doodad() };
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(juror, false);
        await Assert.That(_trials.GetTrialChatRecipients(trial, juror)).IsEmpty();
        await Assert.That(_trials.GetTrialChatRecipients(trial, trial.Defendant).Length).IsEqualTo(1);
        trial.Jury[1].JuryMember = null;
        await Assert.That(_trials.GetParticipatingTrial(juror)).IsNull();
        await Assert.That(new ChatManager().GetTrialChat(juror)).IsNull();
    }

    [Test]
    public async Task JoinTrialAudience_AnotherCourt_RemovesOldMembershipBeforeNewJoin()
    {
        var trial = Trial();
        var audience = Player(3);
        _trials.JoinTrialAudience(audience, 1);
        var other = new TrialCourtRoom { Id = 2, AudienceSeats = [new Doodad { TemplateId = 2 }], TrialChatChannel = new() };
        _trials.CourtRooms.Add(2, other);

        _trials.JoinTrialAudience(audience, 2);

        await Assert.That(trial.CourtRoom.AudienceMembers.Contains(audience)).IsFalse();
        await Assert.That(trial.CourtRoom.TrialChatChannel.GetMembersSnapshot()).IsEmpty();
        await Assert.That(other.AudienceMembers.Contains(audience)).IsTrue();
        await Assert.That(other.TrialChatChannel.GetMembersSnapshot().Single()).IsSameReferenceAs(audience);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task JoinTrialAudience_RoleInAnotherUnreleasedCase_DoesNotJoin(bool juror, bool terminal)
    {
        var trial = Trial();
        var player = juror ? Player(2) : trial.Defendant;
        if (juror)
            trial.Jury[1] = new TrialJuryBox { JuryMember = player, Seat = new Doodad() };
        if (terminal)
            trial.Step = TrialStep.EndTrial;
        var other = new TrialCourtRoom { Id = 2, AudienceSeats = [new Doodad { TemplateId = 2 }], TrialChatChannel = new() };
        _trials.CourtRooms.Add(other.Id, other);

        _trials.JoinTrialAudience(player, 2);

        await Assert.That(other.AudienceMembers).IsEmpty();
        await Assert.That(other.TrialChatChannel.GetMembersSnapshot()).IsEmpty();
        await Assert.That(_trials.GetParticipatingTrial(player)).IsSameReferenceAs(trial);
    }

    [Test]
    public async Task JoinTrialAudience_PendingDefendantInSameCourt_DoesNotJoinDifferentCase()
    {
        var pending = Trial();
        pending.Step = TrialStep.DefendantAwaitingTrial;
        var cases = (ConcurrentDictionary<uint, TrialData>)typeof(TrialManager)
            .GetProperty("Trials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_trials)!;
        cases.TryAdd(pending.Id, pending);
        pending.CourtRoom.CurrentTrial = new TrialData { Id = 2, CourtRoom = pending.CourtRoom, Defendant = Player(3) };

        _trials.JoinTrialAudience(pending.Defendant, 1);

        await Assert.That(pending.CourtRoom.AudienceMembers).IsEmpty();
    }

    private TrialData Trial()
    {
        var room = new TrialCourtRoom { Id = 1, TrialChatChannel = new(), AudienceSeats = [new Doodad { TemplateId = 1 }] };
        var trial = new TrialData { Id = 1, CourtRoom = room, Step = TrialStep.AwaitingJurySummons, Defendant = Player(1) };
        room.CurrentTrial = trial;
        _trials.CourtRooms.Add(room.Id, room);
        return trial;
    }

    private WorldInstance World(uint id)
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1, Name = "crime-access-test" }, 0, true, id);
        _worlds.Add(world);
        GetField<ConcurrentDictionary<uint, WorldInstance>>(_worldManager, "_worlds")[id] = world;
        return world;
    }

    private static TestCharacter Player(uint id) => new() { Id = id, ObjId = id, IsOnline = true, Name = "test" };

    private static void MakePrisoner(Character player, bool activeBuff)
    {
        if (!activeBuff)
        {
            player.OfflineGuiltyTime = 1;
            return;
        }
        var template = new BuffTemplate { Id = 1 };
        var buff = new Buff(player, player, new SkillCasterUnit(), template, null, DateTime.UtcNow);
        GetField<List<Buff>>(player.Buffs, "_effects").Add(buff);
    }

    private static InstantGame Game(Character player, bool entered)
    {
        var game = (InstantGame)RuntimeHelpers.GetUninitializedObject(typeof(InstantGame));
        SetField(game, "_players", new List<Character> { player });
        SetField(game, "_corps", new Dictionary<uint, List<Character>> { [1] = [player] });
        SetField(game, "_characterCorps", new Dictionary<Character, InstantCorps> { [player] = InstantCorps.Corps1 });
        SetField(game, "_members", entered
            ? new Dictionary<Character, InstantGameTeamMember> { [player] = new() { Character = player } }
            : new Dictionary<Character, InstantGameTeamMember>());
        player.OriginFaction = new SystemFaction { Id = FactionsEnum.NuiaAlliance };
        player.CurrentInstantGame = game;
        return game;
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        _restore.Add(() => field.SetValue(null, previous));
        field.SetValue(null, instance);
    }

    private static T GetField<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void SetField(object instance, string name, object value) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class TestCharacter() : Character(null)
    {
        public int FactionChanges { get; private set; }
        public FactionsEnum LastFaction { get; private set; }
        public override void SetFaction(FactionsEnum factionId)
        {
            LastFaction = factionId;
            FactionChanges++;
        }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }
}
