using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class TrialSentenceLifecycleTests
{
    private readonly List<Action> _restore = [];
    private readonly Dictionary<uint, Session> _sessions = [];
    private TrialManager _manager;
    private WorldInstance _world;
    private TrialCourtRoom _court;
    private ConcurrentDictionary<uint, TrialData> Trials => Get<ConcurrentDictionary<uint, TrialData>>(_manager, "<Trials>k__BackingField");

    [Before(Test)]
    public void SetUp()
    {
        var previousJustice = AppConfiguration.Instance.Justice;
        _restore.Add(() => AppConfiguration.Instance.Justice = previousJustice);
        AppConfiguration.Instance.Justice = new JusticeConfig
        {
            AllowJuryEscape = true,
            Jails = [new JailConfig { Faction = FactionsEnum.NuiaAlliance, Pos = new WorldSpawnPosition { WorldId = 1, X = 30, Y = 40, Z = 50 } }]
        };
        var skills = EmptyDictionaries(new SkillManager(null, null));
        var templates = Get<Dictionary<uint, BuffTemplate>>(skills, "_buffs");
        foreach (var id in new[] { BuffConstants.Prisoner_Nuian, BuffConstants.Prisoner_Haranyan,
                     BuffConstants.Wanted, BuffConstants.Contemptuous, BuffConstants.Jury, BuffConstants.CourtHouse,
                     BuffConstants.CannotEscapeBuff })
            templates[(uint)id] = new BuffTemplate { Id = (uint)id, Kind = BuffKind.Bad,
                SaveRuleId = BuffSaveRuleType.Normal, StackRule = BuffStackRule.Refresh };
        var tags = Get<Dictionary<uint, List<uint>>>(skills, "_taggedBuffs");
        tags[(uint)BuffConstants.TagPrisoner] = [(uint)BuffConstants.Prisoner_Nuian, (uint)BuffConstants.Prisoner_Haranyan];
        tags[(uint)BuffConstants.TagWanted] = [(uint)BuffConstants.Wanted];
        Install(skills);
        Install(EmptyDictionaries(new BuffGameData()));
        Install(EmptyDictionaries(new FormulaManager()));
        Install(EmptyDictionaries(new ModelManager()));
        Install(EmptyDictionaries(new FriendMananger()));
        Install(new AccountManager(null, null, TimeProvider.System));
        Install(new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        Install(new TaskManager(Mock.Of<ITickManager>().Object));
        Install(new CrimeManager());
        Install(new InstantGameManager());
        var worlds = new WorldManager(new TickManager(), Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        Install(worlds);
        Install(new SusManager(worlds));
        _world = new WorldInstance(new WorldTemplate { Id = 1, Name = "trial-sentence-test" }, 0, true, 0);
        Get<ConcurrentDictionary<uint, WorldInstance>>(worlds, "_worlds")[0] = _world;
        worlds.MainWorld = _world;
        var ids = new TrialIdManager();
        ids.Initialize();
        var idField = typeof(TrialIdManager).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousIds = idField.GetValue(null);
        _restore.Add(() => idField.SetValue(null, previousIds));
        idField.SetValue(null, ids);
        _manager = new TrialManager();
        Install(_manager);
        _court = new TrialCourtRoom { Id = 1, Region = CourtRoomRegion.Nuian, Faction = FactionsEnum.NuiaAlliance,
            TrialChatChannel = new ChatChannel(), Jail = new WorldSpawnPosition { WorldId = 1, X = 10, Y = 20, Z = 30 } };
        _manager.CourtRooms.Add(1, _court);
    }

    [After(Test)]
    public void TearDown()
    {
        GC.SuppressFinalize(_world);
        foreach (var restore in _restore.AsEnumerable().Reverse())
            restore();
    }

    [Test]
    public async Task LoginWithoutJuryEligibility_RestoresStoredSentenceWithoutRecalculation()
    {
        var player = Player(10);
        player.JuryPoint = 0;
        player.SetPendingTrialSentence(37, CourtRoomRegion.Nuian);

        _manager.HandlePlayerLogin(player);

        await Assert.That(player.HasPendingTrial).IsFalse();
        await Assert.That(player.GetUnservedPrisonMilliseconds()).IsBetween(2_219_000, 2_220_000);
        await Assert.That(player.GuiltyCount).IsEqualTo(1);
        await Assert.That(Trials.Values.Single().JailTime).IsEqualTo(37);
    }

    [Test]
    public async Task EscapedPrisonerWithoutWanted_CanBeArrestedOnce_AndKeepsOldSentence()
    {
        var player = Player(10);
        AddBuff(player, BuffConstants.Prisoner_Nuian, 90_000);
        var arrestor = Player(11);

        var trial = _manager.ArrestCriminal(player, arrestor);
        var duplicate = _manager.ArrestCriminal(player, arrestor);

        await Assert.That(trial).IsNotNull();
        await Assert.That(duplicate).IsNull();
        await Assert.That(player.ArrestCount).IsEqualTo(1);
        await Assert.That(player.Buffs.CheckBuff((uint)BuffConstants.Wanted)).IsFalse();
        await Assert.That(player.GetUnservedPrisonMilliseconds()).IsBetween(89_000, 90_000);
        await Assert.That(player.OfflineGuiltyTime).IsEqualTo(-1);
    }

    [Test]
    [Arguments(1, 0)] [Arguments(3, 180_000)]
    public async Task Verdict_AppliesNewTierBeforeOldSentence_AndSendsTheResult(int verdict, int newMilliseconds)
    {
        var player = Player(10);
        var oldBuff = AddBuff(player, BuffConstants.Prisoner_Nuian, 90_000);
        var trial = Trial(player, TrialStep.JuryVerdict);
        trial.JailTime = 7;
        trial.Jury[0] = new TrialJuryBox { SeatId = 0, JuryMember = Player(11), SelectedSentence = verdict };
        player.SetPendingTrialSentence(7, CourtRoomRegion.Nuian);

        trial.FinalizeVerdict();
        trial.FinalizeVerdict();

        await Assert.That(player.HasPendingTrial).IsFalse();
        await Assert.That(player.GetUnservedPrisonMilliseconds()).IsBetween(89_000 + newMilliseconds, 90_000 + newMilliseconds);
        var result = _sessions[player.Id].Body(SCOffsets.SCRulingStatusPacket);
        result.Rollback();
        await Assert.That(result.ReadInt32()).IsEqualTo(1);
        await Assert.That(result.ReadInt32()).IsEqualTo(1);
        await Assert.That(result.ReadByte()).IsEqualTo((byte)verdict);
        var duration = result.ReadInt32();
        if (verdict == 1)
        {
            await Assert.That(duration).IsEqualTo(0);
            await Assert.That(player.Buffs.GetEffectFromBuffId((uint)BuffConstants.Prisoner_Nuian)).IsSameReferenceAs(oldBuff);
            await Assert.That(player.NotGuiltyCount).IsEqualTo(1);
        }
        else
        {
            await Assert.That(duration).IsBetween(89_000 + newMilliseconds, 90_000 + newMilliseconds);
            await Assert.That(player.GuiltyCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task DefendantDisconnect_RemovesEscapedJurorBuff_AndPreservesPendingMinutes()
    {
        var player = Player(10);
        player.SetPendingTrialSentence(37, CourtRoomRegion.Nuian);
        var trial = Trial(player, TrialStep.ConfirmCriminalRecord);
        var juror = Player(11);
        AddBuff(juror, BuffConstants.Jury);
        trial.Jury[0] = new TrialJuryBox { SeatId = 0, JuryMember = juror };

        _manager.HandlePlayerDisconnect(player);
        _manager.HandlePlayerDisconnect(player);

        await Assert.That(juror.Buffs.CheckBuff((uint)BuffConstants.Jury)).IsFalse();
        await Assert.That(juror.JuryPoint).IsEqualTo(0);
        await Assert.That(player.OfflineGuiltyTime).IsEqualTo(37);
        await Assert.That(Trials).IsEmpty();
        await Assert.That(_sessions[juror.Id].Count(SCOffsets.SCTrialCanceledPacket)).IsEqualTo(1);
    }

    [Test]
    public async Task SuccessfulJurySeat_RemovesPreviousAudienceMembershipBeforeNewPackets()
    {
        var defendant = Player(10);
        var trial = Trial(defendant, TrialStep.AwaitingJurySummons);
        var juror = Player(11);
        var oldCourt = new TrialCourtRoom { Id = 2, TrialChatChannel = new ChatChannel() };
        oldCourt.AudienceMembers.Add(juror);
        oldCourt.TrialChatChannel.JoinChannel(juror);
        _manager.CourtRooms.Add(oldCourt.Id, oldCourt);
        trial.Jury[0] = new TrialJuryBox { SeatId = 0, Seat = new Doodad { TemplateId = 4937, ObjId = 100 } };

        bool joined;
        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
            joined = trial.SummonJuryMember(juror);

        await Assert.That(joined).IsTrue();
        await Assert.That(oldCourt.AudienceMembers.Contains(juror)).IsFalse();
        await Assert.That(oldCourt.TrialChatChannel.GetMembersSnapshot()).IsEmpty();
        await Assert.That(trial.Jury[0].JuryMember).IsSameReferenceAs(juror);
        await Assert.That(_sessions[juror.Id].Count(SCOffsets.SCSummonJuryPacket)).IsEqualTo(1);
    }

    [Test]
    public async Task NoJuryCancel_AppliesFullDefaultOnce()
    {
        var player = Player(10);
        player.SetPendingTrialSentence(7, CourtRoomRegion.Nuian);
        var trial = Trial(player, TrialStep.AwaitingJurySummons);
        trial.JailTime = 7;

        _manager.CancelTrial(player, trial.Id);
        _manager.CancelTrial(player, trial.Id);

        await Assert.That(player.GetUnservedPrisonMilliseconds()).IsBetween(419_000, 420_000);
        await Assert.That(player.AcceptGuiltyCount).IsEqualTo(1);
        await Assert.That(_sessions[player.Id].Count(SCOffsets.SCTrialCanceledPacket)).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false, false, false)] [Arguments(true, false, false, true)]
    [Arguments(true, true, false, true)] [Arguments(true, true, true, false)]
    public async Task PlayerDeath_UsesWantedAndPirateZoneRules(bool wanted, bool pirate, bool pirateZone, bool arrest)
    {
        var player = Player(10);
        var killer = Player(11);
        if (wanted) AddBuff(player, BuffConstants.Wanted);
        if (pirate) AddBuff(player, BuffConstants.Contemptuous);
        await Assert.That(player.GetArrestorOnPlayerDeath(killer, pirateZone) == killer).IsEqualTo(arrest);
    }

    [Test]
    public async Task PlayerDeath_ActivePrisonerUsesPetOwner_EvenInPirateZone()
    {
        var player = Player(10);
        AddBuff(player, BuffConstants.Prisoner_Nuian, 90_000);
        AddBuff(player, BuffConstants.Contemptuous);
        var killer = Player(11);
        await Assert.That(player.GetArrestorOnPlayerDeath(new OwnedUnit(killer), true)).IsSameReferenceAs(killer);
        await Assert.That(player.GetArrestorOnPlayerDeath(new OwnedUnit(player), true)).IsNull();
        await Assert.That(player.GetArrestorOnPlayerDeath(new Unit(), true)).IsNull();
        await Assert.That(player.GetArrestorOnPlayerDeath(null, true)).IsNull();
    }

    [Test]
    [Arguments(true)] [Arguments(false)]
    public async Task PlayerDeath_MateOwnerObjectId_MustMatchPersistentOwner(bool matches)
    {
        var player = Player(10);
        AddBuff(player, BuffConstants.Wanted);
        var killer = Player(11);
        _world.AddObject(killer);
        var mate = new Mate { OwnerObjId = killer.ObjId, OwnerId = matches ? killer.Id : 999, ParentWorld = _world };
        await Assert.That(player.GetArrestorOnPlayerDeath(mate, false) == killer).IsEqualTo(matches);
    }

    private TrialData Trial(Character player, TrialStep step)
    {
        var trial = new TrialData { Id = 100, Defendant = player, DefendantId = player.Id, CourtRoom = _court,
            CourtRegion = CourtRoomRegion.Nuian, Step = step };
        Trials[trial.Id] = trial;
        _court.CurrentTrial = trial;
        return trial;
    }

    private TestCharacter Player(uint id)
    {
        var session = new Session();
        _sessions[id] = session;
        var player = new TestCharacter { Id = id, ObjId = id, Name = $"Trial{id}", IsOnline = true,
            Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance, MotherId = FactionsEnum.NuiaAlliance },
            Connection = new GameConnection(session) };
        player.Connection.ActiveChar = player;
        return player;
    }

    private static Buff AddBuff(Character player, BuffConstants id, int duration = 0)
    {
        player.Buffs.AddBuff((uint)id, player, duration);
        return player.Buffs.GetEffectFromBuffId((uint)id);
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        _restore.Add(() => field.SetValue(null, previous));
        field.SetValue(null, instance);
    }

    private static T EmptyDictionaries<T>(T instance)
    {
        foreach (var field in typeof(T).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                     .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
            field.SetValue(instance, Activator.CreateInstance(field.FieldType));
        return instance;
    }

    private static T Get<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private sealed class OwnedUnit(Character owner) : Unit
    {
        public override Character GetOwnerCharacter() => owner;
    }

    private sealed class TestCharacter() : Character(null)
    {
        public override int MaxHp { get => 100; }
        public override int MaxMp { get => 100; }
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }

    private sealed class Session : ISession
    {
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public int Count(ushort opcode) => Packets.Count(packet => BitConverter.ToUInt16(packet, 6) == opcode);
        public PacketStream Body(ushort opcode) => new PacketStream().Write(Packets.Single(packet => BitConverter.ToUInt16(packet, 6) == opcode)[8..]);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
