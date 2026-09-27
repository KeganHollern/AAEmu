using System.Collections.Concurrent;
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
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Models;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class TrialRulesTests
{
    private TrialManager _manager;
    private object _previousManager;
    private JusticeConfig _previousJustice;
    private object _previousModels;
    private readonly Dictionary<uint, RecordingSession> _sessions = [];
    private ConcurrentDictionary<uint, TrialData> Trials => (ConcurrentDictionary<uint, TrialData>)
        typeof(TrialManager).GetProperty("Trials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_manager)!;

    [Before(Test)]
    public void SetUp()
    {
        _manager = new TrialManager();
        var instance = typeof(Singleton<TrialManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousManager = instance.GetValue(null);
        instance.SetValue(null, _manager);
        var models = new ModelManager();
        typeof(ModelManager).GetField("_modelTypes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(models, new Dictionary<uint, ModelType>());
        var modelsInstance = typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousModels = modelsInstance.GetValue(null);
        modelsInstance.SetValue(null, models);
        _previousJustice = AppConfiguration.Instance.Justice;
        AppConfiguration.Instance.Justice = new JusticeConfig { AllowJuryEscape = true };
    }

    [After(Test)]
    public void TearDown()
    {
        typeof(Singleton<TrialManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, _previousManager);
        AppConfiguration.Instance.Justice = _previousJustice;
        typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, _previousModels);
    }

    [Test]
    [Arguments(0)] [Arguments(7)] [Arguments(127)] [Arguments(255)]
    public async Task Verdict_InvalidOption_DoesNotCountOrEndTheStep(int option)
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        var deadline = trial.CurrentStepEndTime;
        Read(new CSJuryVerdictPacket(), trial.Jury[1].JuryMember,
            new PacketStream().Write(trial.Id).Write(1).Write((byte)option));
        await Assert.That(trial.Jury[1].SelectedSentence).IsEqualTo(-1);
        await Assert.That(trial.CurrentStepEndTime).IsEqualTo(deadline);
        await Assert.That(_sessions.Values.Sum(session => session.Packets.Count)).IsEqualTo(0);
    }

    [Test]
    [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)] [Arguments(5)] [Arguments(6)]
    public async Task Verdict_AuthoredOption_CountsOnce(int option)
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        var juror = trial.Jury[1].JuryMember;
        _manager.JuryVerdict(juror, trial.Id, 1, (byte)option);
        var packetCount = _sessions.Values.Sum(session => session.Packets.Count);
        _manager.JuryVerdict(juror, trial.Id, 1, 6);
        await Assert.That(trial.Jury[1].SelectedSentence).IsEqualTo(option);
        await Assert.That(_sessions.Values.Sum(session => session.Packets.Count)).IsEqualTo(packetCount);
    }

    [Test]
    [Arguments("stranger")] [Arguments("defendant")] [Arguments("other-seat")]
    [Arguments("empty-seat")] [Arguments("missing-seat")] [Arguments("negative-seat")]
    [Arguments("old-character")] [Arguments("wrong-phase")] [Arguments("finished")]
    public async Task Confirmation_RejectsWrongOwnerSeatOrPhase(string scenario)
    {
        var trial = CreateTrial(TrialStep.ConfirmCriminalRecord);
        var sender = trial.Jury[1].JuryMember;
        var seat = 1;
        switch (scenario)
        {
            case "stranger": sender = Player(99); break;
            case "defendant": sender = trial.Defendant; break;
            case "other-seat": sender = trial.Jury[2].JuryMember; break;
            case "empty-seat": trial.Jury[1].JuryMember = null; break;
            case "missing-seat": seat = 9; break;
            case "negative-seat": seat = -1; break;
            case "old-character": sender = Player(sender.Id); break;
            case "wrong-phase": trial.Step = TrialStep.JuryVerdict; break;
            case "finished": trial.TryClaimResult(trial.Defendant); break;
        }
        _manager.JuryEndTestimony(sender, trial.Id, seat);
        await Assert.That(trial.Jury[1].ConfirmTestimony).IsFalse();
        await Assert.That(_sessions.Values.Sum(session => session.Packets.Count)).IsEqualTo(0);
    }

    [Test]
    public async Task Confirmation_EachOwnerConfirmsOnce_EndsOnlyAfterTheLastSeat()
    {
        var trial = CreateTrial(TrialStep.ConfirmCriminalRecord);
        var deadline = trial.CurrentStepEndTime;
        Read(new CSJuryEndTestimonyPacket(), trial.Jury[1].JuryMember,
            new PacketStream().Write(trial.Id).Write(1));
        _manager.JuryEndTestimony(trial.Jury[1].JuryMember, trial.Id, 1);
        await Assert.That(trial.CurrentStepEndTime).IsEqualTo(deadline);
        await Assert.That(_sessions[trial.DefendantId].Packets.Count).IsEqualTo(1);
        _manager.JuryEndTestimony(trial.Jury[2].JuryMember, trial.Id, 2);
        await Assert.That(trial.CurrentStepEndTime <= DateTime.UtcNow).IsTrue();
        await Assert.That(_sessions[trial.DefendantId].Packets.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("unknown")] [Arguments("foreign")]
    [Arguments("juror-present")] [Arguments("record")] [Arguments("closing")]
    [Arguments("verdict")] [Arguments("finished")]
    public async Task Cancel_RejectsUnknownForeignOrStartedCase(string scenario)
    {
        var trial = CreateTrial(TrialStep.AwaitingJurySummons);
        var sender = trial.Defendant;
        var id = trial.Id;
        switch (scenario)
        {
            case "unknown": id++; break;
            case "foreign": sender = trial.Jury[1].JuryMember; break;
            case "record": trial.Step = TrialStep.ConfirmCriminalRecord; break;
            case "closing": trial.Step = TrialStep.ClosingStatement; break;
            case "verdict": trial.Step = TrialStep.JuryVerdict; break;
            case "finished": trial.TryClaimResult(trial.Defendant); break;
        }
        var step = trial.Step;
        Read(new CSCancelTrialPacket(), sender, new PacketStream().Write(id));
        await Assert.That(trial.Step).IsEqualTo(step);
        await Assert.That(trial.CancellationNotified).IsFalse();
        await Assert.That(_sessions.Values.Sum(session => session.Packets.Count)).IsEqualTo(0);
    }

    [Test]
    public async Task Disconnect_CancelsOnce_PreservesPendingSentenceAndReleasesTheRoom()
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        trial.Defendant.OfflineGuiltyTime = 37;
        _manager.HandlePlayerDisconnect(trial.Defendant);
        _manager.HandlePlayerDisconnect(trial.Defendant);
        _manager.UpdateTrialState(trial);
        await Assert.That(_manager.GetTrial(trial.Id)).IsNull();
        await Assert.That(trial.CourtRoom.CurrentTrial).IsNull();
        await Assert.That(trial.Defendant.OfflineGuiltyTime).IsEqualTo(37);
        await Assert.That(trial.Defendant.GuiltyCount).IsEqualTo(0);
        foreach (var session in _sessions.Values)
        {
            await Assert.That(session.Count(SCOffsets.SCTrialCanceledPacket)).IsEqualTo(1);
            await Assert.That(session.Count(SCOffsets.SCRulingClosedPacket)).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(true)] [Arguments(false)]
    public async Task JurorDisconnect_UpdatesTheVoteCountOnceForCurrentParticipants(bool submittedVote)
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        trial.Jury.Add(3, new TrialJuryBox { SeatId = 3, JuryMember = Player(13) });
        var observer = Player(14);
        trial.CourtRoom.AudienceMembers.Add(observer);
        trial.CourtRoom.AudienceSeats.Add(new Doodad { ObjId = 100 });
        var departing = trial.Jury[1].JuryMember;
        if (submittedVote)
            _manager.JuryVerdict(departing, trial.Id, 1, 2);
        _manager.JuryVerdict(trial.Jury[2].JuryMember, trial.Id, 2, 4);
        foreach (var session in _sessions.Values)
            session.Packets.Clear();

        _manager.HandlePlayerDisconnect(departing);
        _manager.HandlePlayerDisconnect(departing);

        await Assert.That(trial.Jury[1].JuryMember).IsNull();
        await Assert.That(trial.Jury[1].SelectedSentence).IsEqualTo(-1);
        await Assert.That(_sessions[departing.Id].Count(SCOffsets.SCChangeJuryVerdictCountPacket)).IsEqualTo(0);
        foreach (var id in new uint[] { trial.DefendantId, 12, 13, observer.Id })
        {
            var bytes = _sessions[id].Packets.Single(packet =>
                BitConverter.ToUInt16(packet, 6) == SCOffsets.SCChangeJuryVerdictCountPacket);
            var body = new PacketStream().Write(bytes[8..]);
            body.Rollback();
            await Assert.That(body.ReadInt32()).IsEqualTo(1);
            await Assert.That(body.ReadInt32()).IsEqualTo(2);
            await Assert.That(body.LeftBytes).IsEqualTo(0);
        }
    }

    [Test]
    public async Task OldTimerAndPackets_CannotChangeAReplacementTrial()
    {
        var old = CreateTrial(TrialStep.JuryVerdict);
        var replacement = new TrialData { Id = old.Id, Defendant = old.Defendant, DefendantId = old.DefendantId,
            CourtRoom = old.CourtRoom, Step = TrialStep.AwaitingJurySummons };
        Trials[old.Id] = replacement;
        old.CourtRoom.CurrentTrial = replacement;
        _manager.UpdateTrialState(old);
        _manager.JuryVerdict(old.Jury[1].JuryMember, old.Id, 1, 6);
        await Assert.That(old.CourtRoom.CurrentTrial).IsEqualTo(replacement);
        await Assert.That(_sessions.Values.Sum(session => session.Packets.Count)).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentVotes_OneSeatProducesOneAcceptedVote()
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            _manager.JuryVerdict(trial.Jury[1].JuryMember, trial.Id, 1, 2))));
        await Assert.That(trial.Jury[1].SelectedSentence).IsEqualTo(2);
        await Assert.That(_sessions[trial.DefendantId].Count(SCOffsets.SCRulingStatusPacket)).IsEqualTo(1);
    }

    [Test]
    public async Task ResultClaim_OnlyOneCallerCanCompleteTheCurrentDefendant()
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        await Assert.That(trial.TryClaimResult(Player(99))).IsFalse();
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => trial.TryClaimResult(trial.Defendant))));
        await Assert.That(results.Count(value => value)).IsEqualTo(1);
        _manager.JuryVerdict(trial.Jury[1].JuryMember, trial.Id, 1, 2);
        await Assert.That(trial.Jury[1].SelectedSentence).IsEqualTo(-1);
    }

    [Test]
    [Arguments(1, 0)] [Arguments(2, 1)] [Arguments(3, 3)]
    [Arguments(4, 5)] [Arguments(5, 7)] [Arguments(6, 8)]
    public async Task SentenceChoices_MatchNativeFloorForSevenMinutes(int choice, int minutes)
    {
        await Assert.That(TrialData.GetSentenceMinutes(7, choice)).IsEqualTo(minutes);
    }

    [Test]
    public async Task StepClock_ClampsExpiredAndInfiniteDeadlines()
    {
        var trial = new TrialData { CurrentStepEndTime = DateTime.UtcNow.AddSeconds(-5) };
        await Assert.That(trial.RemainingCurrentStepTime).IsEqualTo(0u);
        trial.CurrentStepEndTime = DateTime.MaxValue;
        await Assert.That(trial.RemainingCurrentStepTime).IsEqualTo(uint.MaxValue);
    }

    [Test]
    [Arguments("verdict", 9)] [Arguments("confirmation", 8)] [Arguments("cancel", 4)]
    public async Task TruncatedPacket_NeverChangesTrialState(string kind, int size)
    {
        var trial = CreateTrial(kind == "confirmation" ? TrialStep.ConfirmCriminalRecord : TrialStep.JuryVerdict);
        var bytes = new PacketStream().Write(trial.Id).Write(1).Write((byte)2).GetBytes();
        for (var length = 0; length < size; length++)
        {
            var packet = kind switch
            {
                "verdict" => (GamePacket)new CSJuryVerdictPacket(),
                "confirmation" => new CSJuryEndTestimonyPacket(),
                _ => new CSCancelTrialPacket()
            };
            var body = new PacketStream().Write(bytes[..length]);
            Exception error = null;
            try { Read(packet, trial.Jury[1].JuryMember, body); }
            catch (Exception exception) { error = exception; }
            await Assert.That(error).IsNotNull();
        }
        await Assert.That(trial.Jury[1].SelectedSentence).IsEqualTo(-1);
        await Assert.That(trial.Jury[1].ConfirmTestimony).IsFalse();
    }

    [Test]
    [Arguments("verdict", 9)] [Arguments("confirmation", 8)] [Arguments("cancel", 4)]
    public async Task TrailingPacketBytes_AreRejectedBeforeStateChanges(string kind, int size)
    {
        var trial = CreateTrial(kind == "confirmation" ? TrialStep.ConfirmCriminalRecord : TrialStep.JuryVerdict);
        var packet = kind switch
        {
            "verdict" => (GamePacket)new CSJuryVerdictPacket(),
            "confirmation" => new CSJuryEndTestimonyPacket(),
            _ => new CSCancelTrialPacket()
        };
        var bytes = new PacketStream().Write(trial.Id).Write(1).Write((byte)2).GetBytes();
        var body = new PacketStream().Write(bytes[..size]).Write((byte)0);
        await Assert.That(() => Read(packet, trial.Jury[1].JuryMember, body)).Throws<InvalidDataException>();
        await Assert.That(trial.Jury[1].SelectedSentence).IsEqualTo(-1);
        await Assert.That(trial.Jury[1].ConfirmTestimony).IsFalse();
    }

    [Test]
    public async Task FivePersonTrial_LiveCountsAndCancellationReachTheExactParticipants()
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        trial.Jury.Add(3, new TrialJuryBox { SeatId = 3, JuryMember = Player(13) });
        var observer = Player(14);
        trial.CourtRoom.AudienceMembers.Add(observer);
        trial.CourtRoom.AudienceSeats.Add(new Doodad { ObjId = 100 });
        _manager.JuryVerdict(trial.Jury[1].JuryMember, trial.Id, 1, 2);
        foreach (var session in _sessions.Values)
        {
            var bytes = session.Packets.Single(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCChangeJuryVerdictCountPacket);
            var body = new PacketStream().Write(bytes[8..]);
            body.Rollback();
            await Assert.That(body.ReadInt32()).IsEqualTo(1);
            await Assert.That(body.ReadInt32()).IsEqualTo(3);
            await Assert.That(body.LeftBytes).IsEqualTo(0);
        }
        _manager.HandlePlayerDisconnect(trial.Defendant);
        foreach (var session in _sessions.Values)
        {
            await Assert.That(session.Count(SCOffsets.SCTrialCanceledPacket)).IsEqualTo(1);
            await Assert.That(session.Count(SCOffsets.SCRulingClosedPacket)).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("far")] [Arguments("instance")] [Arguments("offline")]
    public async Task InvalidAudience_DoesNotReceiveCountOrCancellation(string state)
    {
        var trial = CreateTrial(TrialStep.JuryVerdict);
        var observer = Player(14);
        trial.CourtRoom.AudienceMembers.Add(observer);
        trial.CourtRoom.AudienceSeats.Add(new Doodad { ObjId = 100 });
        if (state == "far") observer.Transform.Local.SetPosition(6, 0, 0);
        if (state == "instance") typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(observer.Transform, 2u);
        if (state == "offline") typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(observer, false);
        _manager.JuryVerdict(trial.Jury[1].JuryMember, trial.Id, 1, 2);
        _manager.HandlePlayerDisconnect(trial.Defendant);
        await Assert.That(_sessions[14].Packets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task JurySeats_PreservesAllFiveNativeZeroBasedIndices()
    {
        var trial = CreateTrial(TrialStep.AwaitingJurySummons);
        for (var id = 0; id < 5; id++)
            trial.CourtRoom.JurySeats.Add(id, new Doodad { ObjId = (uint)(100 + id) });
        trial.InitializeJurySeats();
        await Assert.That(trial.Jury.Keys.Order().ToArray()).IsEquivalentTo(new[] { 0, 1, 2, 3, 4 });
        foreach (var (id, seat) in trial.Jury)
        {
            await Assert.That(seat.SeatId).IsEqualTo(id);
            await Assert.That(seat.Seat).IsEqualTo(trial.CourtRoom.JurySeats[id]);
            await Assert.That(seat.SelectedSentence).IsEqualTo(-1);
        }
    }

    [Test]
    [Arguments(4937u, true, 0u, 0)] [Arguments(4941u, true, 0u, 4)]
    [Arguments(4942u, true, 1u, 0)] [Arguments(4946u, true, 1u, 4)]
    [Arguments(4947u, false, 0u, 0)] [Arguments(4951u, false, 0u, 4)]
    [Arguments(4952u, false, 1u, 0)] [Arguments(4956u, false, 1u, 4)]
    public async Task NativeChairMapping_UsesContinentBankAndZeroBasedSeat(uint template, bool west, uint bank, int seat)
    {
        await Assert.That(TrialCourtRoom.TryGetNativeJuryLocation(template, out var actualWest, out var actualBank, out var actualSeat)).IsTrue();
        await Assert.That(actualWest).IsEqualTo(west);
        await Assert.That(actualBank).IsEqualTo(bank);
        await Assert.That(actualSeat).IsEqualTo(seat);
    }

    [Test]
    [Arguments(0u)] [Arguments(4936u)] [Arguments(4957u)] [Arguments(uint.MaxValue)]
    public async Task NativeChairMapping_RejectsTemplatesOutsideTheNativeArrays(uint template)
    {
        await Assert.That(TrialCourtRoom.TryGetNativeJuryLocation(template, out _, out _, out _)).IsFalse();
    }

    [Test]
    public async Task NoSubmittedVotes_PreservesLegacyChoiceThreeFallback()
    {
        await Assert.That(TrialData.SelectVerdict([])).IsEqualTo(3);
        await Assert.That(TrialData.GetSentenceMinutes(10, TrialData.SelectVerdict([]))).IsEqualTo(5);
    }

    [Test]
    public async Task SentenceChoices_ClampToNonnegativePacketMilliseconds()
    {
        await Assert.That(TrialData.GetSentenceMinutes(-1, 6)).IsEqualTo(0);
        await Assert.That(TrialData.GetSentenceMinutes(int.MaxValue, 6)).IsEqualTo(int.MaxValue / 60_000);
        await Assert.That(() => TrialData.GetSentenceMinutes(7, 0)).Throws<ArgumentOutOfRangeException>();
    }

    private TrialData CreateTrial(TrialStep step)
    {
        var defendant = Player(10);
        var trial = new TrialData { Id = 50, Defendant = defendant, DefendantId = defendant.Id,
            Step = step, CurrentStepEndTime = DateTime.UtcNow.AddMinutes(1),
            CourtRoom = new TrialCourtRoom { TrialChatChannel = new ChatChannel() } };
        trial.CourtRoom.CurrentTrial = trial;
        trial.Jury.Add(1, new TrialJuryBox { SeatId = 1, JuryMember = Player(11) });
        trial.Jury.Add(2, new TrialJuryBox { SeatId = 2, JuryMember = Player(12) });
        Trials[trial.Id] = trial;
        return trial;
    }

    private Character Player(uint id)
    {
        var session = new RecordingSession();
        _sessions[id] = session;
        var player = new CharacterMock { Id = id, ObjId = id, Name = $"Trial{id}", Connection = new GameConnection(session) };
        player.Connection.ActiveChar = player;
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(player, true);
        return player;
    }

    private static void Read(GamePacket packet, Character player, PacketStream body)
    {
        packet.Connection = player.Connection;
        body.Rollback();
        packet.Read(body);
    }

    private sealed class RecordingSession : ISession
    {
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { lock (Packets) Packets.Add(packet.ToArray()); }
        public int Count(ushort opcode) => Packets.Count(packet => BitConverter.ToUInt16(packet, 6) == opcode);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
