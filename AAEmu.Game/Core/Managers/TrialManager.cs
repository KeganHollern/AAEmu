using System.Collections.Concurrent;
using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Teleport;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Crime;
using NLog;

namespace AAEmu.Game.Core.Managers;

// TODO: Handle/check/create these related errors
// ErrorMessageType.CannotExitWhileInTrial

public class TrialManager : Singleton<TrialManager>, ITrialManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public const int TrialInitSeconds = 10;
    public const int AwaitingJurySummonsMinutes = 2;
    private const int ConfirmCriminalRecordMinutes = 5;
    private const int ClosingStatementMinutes = 1;
    private const int JuryVerdictMinutes = 1;
    private const int EndTrialSeconds = 30;

    private readonly Lock _queueLock = new();
    private readonly object _juryAdmissionLock = new();
    private readonly object _courtAssignmentLock = new();
    private bool _loading;
    private bool _loaded;

    public Dictionary<uint, TrialCourtRoom> CourtRooms { get; } = [];
    private ConcurrentDictionary<uint, TrialData> Trials { get; } = [];

    /// <summary>
    /// PlayerIds of the Jury queues for various factions
    /// </summary>
    private Dictionary<FactionsEnum, List<uint>> JuryQueues { get; } = [];

    public CourtRoomRegion GetCourtRoomRegionByFaction(FactionsEnum faction)
    {
        return faction switch
        {
            FactionsEnum.NuiaAlliance => CourtRoomRegion.Nuian,
            FactionsEnum.HaranyaAlliance => CourtRoomRegion.Haranyan,
            _ => CourtRoomRegion.Invalid
        };
    }

    public void Load()
    {
        if (_loading || _loaded)
            return;
        _loading = true;
        // Default factions for jury, if you ever add more court factions, you will need to add them here.
        JuryQueues.Add(FactionsEnum.NuiaAlliance, []);
        JuryQueues.Add(FactionsEnum.HaranyaAlliance, []);

        foreach (var courtRoomConfig in AppConfiguration.Instance.Justice.CourtRooms)
        {
            var courtRoom = new TrialCourtRoom
            {
                Id = courtRoomConfig.Id,
                Region = GetCourtRoomRegionByFaction(courtRoomConfig.Faction),
                Faction = courtRoomConfig.Faction,
                Name = courtRoomConfig.Name,
                Defendant = courtRoomConfig.Defendant,
                Jail = courtRoomConfig.HoldingCell,
                CurrentTrial = null
            };
            // Seats and judge will be added after spawns
            CourtRooms.Add(courtRoom.Id, courtRoom);
            courtRoom.TrialChatChannel = ChatManager.Instance.GetTrialChat(courtRoom.Region);
        }

        TaskManager.Instance.Schedule(new TrialUpdateTask(), null, TimeSpan.FromSeconds(5));

        Logger.Info($"Loaded trials data");
        _loaded = true;
        _loading = false;
    }

    private bool CanAcceptTrialInvites(Character character)
    {
        // Needs to be online
        if (character == null || !character.IsOnline)
            return false;

        // Needs to have unlocked jury duty
        if (character.JuryPoint <= 0)
            return false;

        // Not sure about this one
        if (character.CrimePoint >= CrimeManager.WantedCrimePointThreshold)
            return false;

        // Cannot be a wanted or bot suspect
        if (character.Buffs.CheckBuffTag((uint)BuffConstants.TagOffender))
            return false;

        // No prisoners allowed
        if (character.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner))
            return false;

        // Faction check is not done here and will prevent the player from actually getting in a queue if needed
        if (IsPlayerInCourt(character.Id) || GetParticipatingTrial(character) != null)
            return false;

        return true;
    }

    /// <summary>
    /// Updates and re-orders the jury queue as needed
    /// Handles disconnected players and those that are not eligible
    /// </summary>
    public void UpdateJuryQueue()
    {
        // Eligibility reads trial state. Do not hold the queue lock while taking a trial lock.
        var eligible = WorldManager.Instance.MainWorld.GetAllCharacters()
            .Where(CanAcceptTrialInvites).ToList();
        Dictionary<FactionsEnum, uint[]> snapshot;
        lock (_queueLock)
            snapshot = JuryQueues.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        var toRemove = new HashSet<uint>();
        foreach (var (faction, queue) in snapshot)
        {
            foreach (var playerId in queue)
            {
                var player = WorldManager.Instance.GetCharacterById(playerId);
                if (!CanAcceptTrialInvites(player) || faction != player.Faction.MotherId)
                    toRemove.Add(playerId);
            }
        }

        lock (_queueLock)
        {
            foreach (var queue in JuryQueues.Values)
                queue.RemoveAll(toRemove.Contains);
            foreach (var player in eligible)
            {
                if (!toRemove.Contains(player.Id) && JuryQueues.TryGetValue(player.Faction.MotherId, out var queue) &&
                    !queue.Contains(player.Id))
                    queue.Add(player.Id);
            }
        }
    }

    /// <summary>
    /// Basic handling of trial flow states
    /// </summary>
    private void UpdateTrialStates()
    {
        foreach (var trial in Trials.Values.ToList())
            UpdateTrialState(trial);
    }

    internal void UpdateTrialState(TrialData trialData)
    {
        lock (SaveManager.PersistenceSyncRoot)
        lock (trialData.SyncRoot)
        {
            if (!IsCurrentTrial(trialData))
                return;

            if (!trialData.ResultApplied && trialData.Defendant?.IsOnline != true)
            {
                CancelDisconnectedDefendant(trialData);
                return;
            }

            // Reserve a free courtroom before any teleport, buff, or packet callback.
            if (trialData.Step == TrialStep.DefendantAwaitingTrial && trialData.DecisionReceived &&
                trialData.CurrentStepEndTime <= DateTime.UtcNow)
            {
                TrialCourtRoom targetCourtRoom;
                lock (_courtAssignmentLock)
                {
                    targetCourtRoom = CourtRooms.Values.FirstOrDefault(
                        room => room.Region == trialData.CourtRegion && room.CurrentTrial == null);
                    if (targetCourtRoom == null)
                        return;
                    targetCourtRoom.CurrentTrial = trialData;
                }
                trialData.EnterCourtRoom(targetCourtRoom, trialData.Defendant);
            }

            if (trialData.Step is > TrialStep.AwaitingJurySummons and < TrialStep.TrialCancelled && trialData.GetActiveJuryCount() <= 0)
            {
                // If no jury left during a trial, auto-cancel it
                trialData.Step = TrialStep.TrialCancelled;
            }

            // If awaiting jury summons and there is nobody left in the queue, then immediately skip waiting for the others
            // This prevents the 2 minutes wait on very low population servers
            if (trialData.Step == TrialStep.AwaitingJurySummons && !QueueAvailable(trialData))
            {
                Logger.Debug($"TrialStep.AwaitingJurySummons skipping jury wait, nobody in queue - {trialData.Id}");
                trialData.CurrentStepEndTime = DateTime.UtcNow;
            }

            // Wait for jury end
            if (trialData.Step == TrialStep.AwaitingJurySummons && trialData.CurrentStepEndTime <= DateTime.UtcNow)
            {
                Logger.Debug($"TrialStep.ConfirmCriminalRecord - {trialData.Id}");
                trialData.Step = TrialStep.ConfirmCriminalRecord;
                trialData.CurrentStepEndTime =
                    DateTime.UtcNow.AddMinutes(ConfirmCriminalRecordMinutes);

                // TODO: find the correct packet and/or text Id to use here, 3.x uses SCChatToken
                trialData.SendPackets(new SCChatMessagePacket(ChatType.Judge, $"Begin the trial of {trialData.DefendantName}"));
                // trialData.SendPackets(new SCNpcChatMessagePacket(ChatType.Judge, trialData.CourtRoom.JudgeSpawner.SpawnedNpcs.Values.FirstOrDefault()?.FirstOrDefault() ?? null, null, 1, 113, trialData.DefendantName));

                // Update the step and send the new active jury count
                trialData.SendPackets(new SCChangeTrialStatePacket(trialData.Id, (byte)trialData.Step,
                    trialData.GetActiveJuryCount(), trialData.RemainingCurrentStepTime));
            }

            // Wait for "give verdict" by jury
            if (trialData.Step == TrialStep.ConfirmCriminalRecord && trialData.CurrentStepEndTime <= DateTime.UtcNow)
            {
                // Goto select verdict
                Logger.Debug($"TrialStep.ClosingStatement - {trialData.Id}");
                trialData.Step = TrialStep.ClosingStatement;
                trialData.CurrentStepEndTime = DateTime.UtcNow.AddMinutes(ClosingStatementMinutes);

                // Update the step
                trialData.SendPackets(new SCChangeTrialStatePacket(trialData.Id, (byte)trialData.Step, trialData.GetActiveJuryCount(), trialData.RemainingCurrentStepTime));
            }

            // Wait for Closing statement of the defendant
            if (trialData.Step == TrialStep.ClosingStatement && trialData.CurrentStepEndTime <= DateTime.UtcNow)
            {
                // Goto select verdict
                Logger.Debug($"TrialStep.JuryVerdict - {trialData.Id}");
                trialData.Step = TrialStep.JuryVerdict;
                trialData.CurrentStepEndTime = DateTime.UtcNow.AddMinutes(JuryVerdictMinutes);

                // Update the step
                trialData.SendPackets(new SCChangeTrialStatePacket(trialData.Id, (byte)trialData.Step, trialData.GetActiveJuryCount(), trialData.RemainingCurrentStepTime));

                // Update jury verdict panel to default
                trialData.Defendant?.SendPacket(new SCRulingStatusPacket(0, trialData.GetActiveJuryCount(), 0, 0));
            }

            if (trialData.Step == TrialStep.JuryVerdict && trialData.CurrentStepEndTime <= DateTime.UtcNow)
            {
                // Goto trial result
                Logger.Debug($"TrialStep.CalculateSentence - {trialData.Id}");
                trialData.FinalizeVerdict();
            }

            if (trialData.Step == TrialStep.EndTrial && trialData.CurrentStepEndTime <= DateTime.UtcNow)
                CompleteTrial(trialData, true);
            else if (trialData.Step == TrialStep.PleadGuilty)
                CompleteTrial(trialData, false);
            else if (trialData.Step == TrialStep.TrialCancelled)
            {
                ResultIsGuilty(trialData.Defendant, trialData, false);
                NotifyTrialCancellation(trialData);
                CompleteTrial(trialData, false);
            }
        }
    }

    private static void NotifyTrialCancellation(TrialData trial)
    {
        if (trial.CancellationNotified)
            return;
        trial.CancellationNotified = true;
        trial.SendPackets(new SCTrialCanceledPacket(trial.Id), true);
    }

    // The durable pending sentence already exists before a trial starts. Do not update an old
    // Character after disconnect, since the final save or a new session can already own its state.
    private void CancelDisconnectedDefendant(TrialData trial)
    {
        if (!trial.ResultApplied)
        {
            trial.TryClaimResult(trial.Defendant);
            trial.Step = TrialStep.TrialCancelled;
            NotifyTrialCancellation(trial);
        }
        CompleteTrial(trial, false);
    }

    public void HandlePlayerDisconnect(Character player)
    {
        if (player == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        {
            RemovePlayerFromQueue(player);
            foreach (var trial in Trials.Values.ToArray())
            {
                lock (trial.SyncRoot)
                {
                    if (!IsCurrentTrial(trial))
                        continue;
                    if (ReferenceEquals(trial.Defendant, player))
                    {
                        CancelDisconnectedDefendant(trial);
                        continue;
                    }
                    var seat = trial.Jury.Values.FirstOrDefault(entry => ReferenceEquals(entry.JuryMember, player));
                    if (seat == null)
                        continue;
                    ReturnJuryMember(trial, seat, false);
                    seat.JuryMember = null;
                    seat.ConfirmTestimony = false;
                    seat.SelectedSentence = -1;
                    if (!trial.ResultApplied && trial.Step == TrialStep.JuryVerdict)
                    {
                        var jury = trial.Jury.Values.Where(entry => entry.JuryMember != null).ToArray();
                        trial.SendPackets(new SCChangeJuryVerdictCountPacket(
                            jury.Count(entry => entry.SelectedSentence is >= 1 and <= 6), jury.Length), true);
                    }
                }
            }
        }
    }

    private void CompleteTrial(TrialData trial, bool awardJuryPoint)
    {
        if (trial.Step >= TrialStep.Cleanup)
            return;
        trial.Step = TrialStep.Cleanup;
        trial.CurrentStepEndTime = DateTime.MaxValue;
        trial.SendPackets(new SCRulingClosedPacket(), true);
        trial.CourtRoom?.TrialChatChannel?.LeaveChannel(trial.Defendant);
        foreach (var seat in trial.Jury.Values)
            ReturnJuryMember(trial, seat, awardJuryPoint);
        lock (_courtAssignmentLock)
        {
            if (trial.CourtRoom?.CurrentTrial == trial)
                trial.CourtRoom.CurrentTrial = null;
        }
        trial.Step = TrialStep.Invalid;
        Trials.TryRemove(new KeyValuePair<uint, TrialData>(trial.Id, trial));
    }

    private static void ReturnJuryMember(TrialData trial, TrialJuryBox seat, bool awardJuryPoint)
    {
        var player = seat.JuryMember;
        if (player == null)
            return;
        if (AppConfiguration.Instance.Justice.AllowJuryEscape && !player.Buffs.CheckBuff((uint)BuffConstants.CourtHouse))
        {
            player.MainWorldPosition = null;
        }
        else
        {
            var position = player.MainWorldPosition?.World.Position ?? player.Transform.World.Position;
            player.ForceDismount();
            player.DisabledSetPosition = true;
            player.Transform.World.SetPosition(position);
            player.SendPacket(new SCTeleportUnitPacket(TeleportReason.Jury, 0,
                position.X, position.Y, position.Z, player.MainWorldPosition?.World.Rotation.Z ?? 0));
            player.MainWorldPosition = null;
            player.Buffs.RemoveBuff((uint)BuffConstants.CourtHouse);
            player.Buffs.RemoveBuff((uint)BuffConstants.Jury);
            if (awardJuryPoint)
            {
                player.JuryPoint++;
                player.Achievements.Increment(CharRecordKind.GetJuryPoint, 0, 0);
            }
        }
        trial.CourtRoom?.TrialChatChannel?.LeaveChannel(player);
    }

    /// <summary>
    /// Checks if there are still people in the queue pool for this trial's faction
    /// </summary>
    /// <param name="trial"></param>
    /// <returns></returns>
    private bool QueueAvailable(TrialData trial)
    {
        lock (_queueLock)
            return JuryQueues.TryGetValue(trial.CourtRoom.Faction, out var queue) && queue.Count > 0;
    }

    public void GetJuryQueueForPlayer(Character player)
    {
        int position;
        lock (_queueLock)
        {
            if (!JuryQueues.TryGetValue(player.Faction.MotherId, out var queue))
                return;
            position = queue.IndexOf(player.Id);
        }
        if (position >= 0)
            player.SendPacket(new SCJuryWaitingNumberPacket(position + 1));
    }

    public void RemovePlayerFromQueue(Character player)
    {
        lock (_queueLock)
        {
            foreach (var queue in JuryQueues.Values)
                queue.Remove(player.Id);
        }
    }

    public List<Character> GenerateJuryList(TrialData trial, int maxCount = 10)
    {
        uint[] candidates;
        lock (_queueLock)
        {
            if (!JuryQueues.TryGetValue(trial.CourtRoom.Faction, out var queue))
                return [];
            candidates = queue.ToArray();
        }
        return candidates.Select(WorldManager.Instance.GetCharacterById)
            .Where(player => player != null && player.IsOnline && player.Id != trial.DefendantId)
            .Take(maxCount).ToList();
    }

    /// <summary>
    /// Handles the reply for a trial invite
    /// </summary>
    /// <param name="player"></param>
    /// <param name="accept"></param>
    /// <param name="trialId"></param>
    /// <returns></returns>
    public bool ProcessTrialInviteReply(Character player, bool accept, uint trialId)
    {
        if (player == null)
            return false;

        // Reserve admission across trials before taking a single trial's lock. No trial callback
        // takes this lock, and queue eligibility checks never hold the queue lock.
        lock (SaveManager.PersistenceSyncRoot)
        lock (_juryAdmissionLock)
        {
            var trial = GetTrial(trialId);
            if (trial == null)
            {
                player.SendErrorMessage(ErrorMessageType.TrialsAlreadyClosed);
                return false;
            }

            lock (_queueLock)
            {
                if (!JuryQueues.TryGetValue(player.Faction.MotherId, out var queue) || !queue.Contains(player.Id))
                {
                    player.SendErrorMessage(ErrorMessageType.TrialsJuryFull);
                    return false;
                }
                if (!accept)
                {
                    queue.Remove(player.Id);
                    return false;
                }
            }

            if (!CanAcceptTrialInvites(player))
            {
                player.SendErrorMessage(ErrorMessageType.TrialsJuryFull);
                return false;
            }
            if (player.ParentWorld?.Id != WorldManager.DefaultInstanceId)
            {
                player.SendErrorMessage(ErrorMessageType.CannotJoinTrialFromInstantZone);
                return false;
            }

            lock (trial.SyncRoot)
            {
                if (!IsCurrentTrial(trial) || trial.Step != TrialStep.AwaitingJurySummons || trial.ResultApplied)
                {
                    player.SendErrorMessage(ErrorMessageType.TrialsCannotJoinAfterStart);
                    return false;
                }
                var joined = trial.SummonJuryMember(player);
                if (!joined)
                    player.SendErrorMessage(ErrorMessageType.TrialsJuryFull);
                return joined;
            }
        }
    }

    /// <summary>
    /// Called every few seconds
    /// </summary>
    public void UpdateTick()
    {
        UpdateJuryQueue();
        UpdateTrialStates();
    }


    /// <summary>
    /// Call this when spawning the initial world Doodads and NPCs
    /// </summary>
    /// <param name="world"></param>
    public void InitializeDoodads(WorldInstance world)
    {
        var justiceDoodads = world.SpawnManager.GetSpecialDoodadSpawners("justice.");

        foreach (var (courtRoomId, trialCourtRoom) in CourtRooms)
        {
            foreach (var doodadSpawner in justiceDoodads)
            {
                // Jury seats
                for (var seatId = 1; seatId <= 5; seatId++)
                {
                    if (doodadSpawner.SpecialLink == $"justice.{courtRoomId}.seat.{seatId}")
                    {
                        trialCourtRoom.JurySeats.Add(seatId - 1, doodadSpawner.Last);
                    }
                }

                // Audience seats
                if (doodadSpawner.SpecialLink == $"justice.{courtRoomId}.spectator")
                {
                    trialCourtRoom.AudienceSeats.Add(doodadSpawner.Last);
                }
            }
        }
    }

    /// <summary>
    /// Call this when spawning the initial world Doodads and NPCs
    /// </summary>
    /// <param name="world"></param>
    public void InitializeNpc(WorldInstance world)
    {
        var justiceNpcs = world.SpawnManager.GetSpecialNpcSpawners("justice.");

        foreach (var (courtRoomId, trialCourtRoom) in CourtRooms)
        {
            // NPCs
            foreach (var npcSpawner in justiceNpcs)
            {
                // Judge
                if (npcSpawner.SpecialLink == $"justice.{courtRoomId}.judge" && trialCourtRoom.JudgeSpawner == null)
                {
                    trialCourtRoom.JudgeSpawner = npcSpawner;
                    break;
                }
            }
        }
    }

    public void HandlePlayerLogin(Character player)
    {
        // Sentence recovery must not depend on eligibility to serve on somebody else's jury.
        if (player.HasPendingTrial)
        {
            var trial = ArrestCriminal(player, null);
            if (trial != null)
                ResultIsGuilty(player, trial, false);
            else
                Logger.Error($"Failed to recover pending sentence for {player.Name} ({player.Id}): {player.OfflineGuiltyTime}, {player.OfflineGuiltyRegion}");
            return;
        }

        if (!CanAcceptTrialInvites(player))
            return;
        lock (_queueLock)
        {
            if (JuryQueues.TryGetValue(player.Faction.MotherId, out var queue) && !queue.Contains(player.Id))
                queue.Add(player.Id);
        }
    }

    /// <summary>
    /// Handles the functions and packets that need to happen if a wanted criminal gets killed by a player
    /// </summary>
    /// <param name="criminal"></param>
    /// <param name="arrestor"></param>
    public TrialData ArrestCriminal(Character criminal, Character arrestor)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (criminal == null || IsPlayerInCourt(criminal.Id))
                return null;

            var recovering = arrestor == null && criminal.HasPendingTrial;
            var storedMinutes = Math.Max(0, criminal.OfflineGuiltyTime);
            var criminalCourtRegion = recovering ? criminal.OfflineGuiltyRegion : criminal.GetPrisonCourtRegion();
            if (criminalCourtRegion == CourtRoomRegion.Invalid)
                criminalCourtRegion = GetCourtRoomRegionByFaction(criminal.Faction.MotherId);
            var arrestorCourtRegion = arrestor != null ? GetCourtRoomRegionByFaction(arrestor.Faction.MotherId) :
                GetCourtRoomRegionByFaction(criminal.Faction.MotherId);
            if (criminalCourtRegion == CourtRoomRegion.Invalid)
                criminalCourtRegion = arrestorCourtRegion;
            var courtRoom = CourtRooms.Values.FirstOrDefault(c => c.Region == criminalCourtRegion);
            if (courtRoom == null)
            {
                Logger.Warn($"Failed to find a court room for {criminal.Name}");
                return null;
            }

            LeaveTrialAudience(criminal);
            var trial = CreateTrialCase(criminal, courtRoom, recovering ? storedMinutes : null);
            if (trial == null)
                return null;
            criminal.Buffs.RemoveBuff((uint)BuffConstants.Wanted);
            if (arrestor != null)
            {
                criminal.ArrestCount++;
                criminal.BroadcastPacket(new SCCriminalArrestedPacket(criminal.ObjId, criminal.Name, arrestor.Name), true);
            }
            if (!recovering)
                trial.EnterCourtJail(courtRoom, criminal);
            return trial;
        }
    }

    /// <summary>
    /// Creates a trial case to be used
    /// </summary>
    /// <param name="defendant"></param>
    /// <returns></returns>
    private TrialData CreateTrialCase(Character defendant, TrialCourtRoom courtRoom, int? storedMinutes)
    {
        var trial = new TrialData
        {
            DefendantId = defendant.Id,
            DefendantName = defendant.Name,
            Id = TrialIdManager.Instance.GetNextId(),
            Step = TrialStep.DefendantAwaitingTrial,
            CurrentStepEndTime = DateTime.MaxValue,
            Defendant = defendant,
            CourtRegion = courtRoom.Region,
            CourtRoom = courtRoom
        };
        trial.EvidenceList = CrimeManager.Instance.GetCrimesOfPlayer(defendant.Id, false);
        if (storedMinutes.HasValue)
        {
            trial.JailTime = storedMinutes.Value;
            defendant.SetPendingTrialSentence(trial.JailTime, courtRoom.Region);
        }
        else
            trial.CalculateJailTime();
        return Trials.TryAdd(trial.Id, trial) ? trial : null;
    }

    /// <summary>
    /// Generates a list of packets that contains the evidence data for trials
    /// </summary>
    /// <param name="trialId"></param>
    /// <param name="unk1"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static List<SCCrimeRecordsPacket> GenerateCrimeRecordsPacketList(uint trialId, uint unk1, List<CrimeEvent> events)
    {
        var res = new List<SCCrimeRecordsPacket>();

        if (events.Count == 0)
            return res;

        var start = 0;
        while (start < events.Count)
        {
            var rest = Math.Min(events.Count - start, 5);
            var eventBlocks = events.Slice(start, rest);
            res.Add(new SCCrimeRecordsPacket(trialId, unk1, events.Count, rest, eventBlocks));
            start += 5;
        }

        return res;
    }

    private TrialData GetTrialCase(uint defendantId)
    {
        return Trials.Values.FirstOrDefault(x => x.DefendantId == defendantId);
    }

    public void AddNewEvidence(CrimeEvent newEvent)
    {
        var trial = GetTrialCase(newEvent.Criminal);
        if (trial == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || trial.ResultApplied || trial.Step >= TrialStep.TrialCancelled)
                return;
            trial.EvidenceList.Add(newEvent);
            trial.CalculateJailTime();
            trial.SendPackets(new SCCrimeRecordsPacket(trial.Id, 0, trial.EvidenceList.Count, 1, [newEvent]));
        }
    }

    public void ReplyImprisonOrTrial(Character defendant, bool requestTrial)
    {
        var trial = defendant == null ? null : GetTrialCase(defendant.Id);
        if (trial == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || trial.Defendant != defendant || trial.ResultApplied ||
                trial.Step != TrialStep.DefendantAwaitingTrial || trial.DecisionReceived)
                return;

            trial.DecisionReceived = true;
            if (!requestTrial)
            {
                ResultIsGuilty(defendant, trial, true);
                return;
            }

            defendant.AcceptTrialCount++;
            trial.CurrentStepEndTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Sends the defendant to actual jail
    /// </summary>
    /// <param name="defendant"></param>
    /// <param name="trial"></param>
    /// <param name="pleadGuilty"></param>
    public void ResultIsGuilty(Character defendant, TrialData trial, bool pleadGuilty)
    {
        if (trial == null || defendant == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || !trial.TryClaimResult(defendant))
                return;

            Logger.Debug($"TrialStep.EndTrial - {trial.Id}");
            trial.Step = pleadGuilty ? TrialStep.PleadGuilty : TrialStep.EndTrial;
            trial.CurrentStepEndTime = DateTime.UtcNow.AddSeconds(EndTrialSeconds);
            trial.SendPackets(new SCChangeTrialStatePacket(trial.Id, (byte)trial.Step, trial.GetActiveJuryCount(), trial.RemainingCurrentStepTime));
            trial.CourtRoom.TrialChatChannel.LeaveChannel(defendant);

            // Find jail to go to
            var targetJail = AppConfiguration.Instance.Justice.Jails.FirstOrDefault(x => x.Faction == trial.CourtRoom.Faction);
            if (targetJail == null)
            {
                Logger.Error($"Could not find a jail location for Court: {trial.CourtRoom.Name}, Defendant: {defendant?.Name}");
                return;
            }

            if (!defendant.ApplyPrisonSentence(trial.CourtRegion, trial.JailTime))
            {
                Logger.Error($"Could not apply prison sentence for {defendant.Id} in {trial.CourtRegion}");
                return;
            }

            trial.ArchiveEvidence();
            var crimeCount = defendant.CrimePoint;
            defendant.CrimePoint -= crimeCount;
            defendant.Buffs.RemoveBuff((uint)BuffConstants.Wanted);
            // Update counters
            if (pleadGuilty)
            {
                defendant.AcceptGuiltyCount++;
            }
            else
            {
                defendant.GuiltyCount++;
            }
            defendant.SendPacket(new SCCrimeChangedPacket(crimeCount, defendant.CrimePoint, defendant.InfamyPoint,
                defendant.GetCrimeState()));

            if (!pleadGuilty)
                defendant.Achievements.Increment(CharRecordKind.Judgement, 1, 0);

            PrisonerAccess.MoveToJusticeDestination(defendant, targetJail.Pos, TeleportReason.Jail);
            defendant.Buffs.RemoveBuff((uint)BuffConstants.Trial_Defendant);
        }
    }

    /// <summary>
    /// Frees the defendant
    /// </summary>
    /// <param name="defendant"></param>
    /// <param name="trial"></param>
    public void ResultIsNotGuilty(Character defendant, TrialData trial)
    {
        if (trial == null || defendant == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || !trial.TryClaimResult(defendant))
                return;

            Logger.Debug($"TrialStep.EndTrial - {trial.Id}");
            trial.Step = TrialStep.EndTrial;
            trial.CurrentStepEndTime = DateTime.UtcNow.AddSeconds(EndTrialSeconds);
            trial.SendPackets(new SCChangeTrialStatePacket(trial.Id, (byte)trial.Step, trial.GetActiveJuryCount(), trial.RemainingCurrentStepTime));

            // Update evidence
            trial.ArchiveEvidence();

            // Update crime points
            var crimeCount = defendant.CrimePoint;
            defendant.ClearPendingTrialSentence();
            defendant.NotGuiltyCount++;
            defendant.CrimePoint -= crimeCount;
            defendant.InfamyPoint -= crimeCount;
            defendant.Buffs.RemoveBuff((uint)BuffConstants.Wanted);
            defendant.SendPacket(new SCCrimeChangedPacket(crimeCount, defendant.CrimePoint, defendant.InfamyPoint, defendant.GetCrimeState()));
            defendant.Achievements.Increment(CharRecordKind.Judgement, 0, 0);

            defendant.Buffs.RemoveBuff((uint)BuffConstants.Trial_Defendant);
            if (defendant.GetUnservedPrisonMilliseconds() > 0)
            {
                // Acquittal of the new charge does not erase an earlier conviction.
                var jail = AppConfiguration.Instance.Justice.Jails.FirstOrDefault(x => x.Faction == trial.CourtRoom.Faction);
                if (jail != null)
                    PrisonerAccess.MoveToJusticeDestination(defendant, jail.Pos, TeleportReason.Jail);
            }
            else
                defendant.Buffs.RemoveBuffs(BuffKind.Bad, 1, (uint)BuffConstants.TagPrisoner);

            defendant.TryLeavePirateFactionAfterRehabilitation();
        }
    }

    public TrialData GetTrial(uint trialId)
    {
        return Trials.GetValueOrDefault(trialId);
    }

    /// <summary>
    /// Marks a jury member as having confirmed the criminal record
    /// </summary>
    /// <param name="juryMember"></param>
    /// <param name="trial"></param>
    /// <param name="juryId"></param>
    public void JuryEndTestimony(Character juryMember, uint trialId, int juryId)
    {
        var trial = GetTrial(trialId);
        if (trial == null)
            return;

        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || trial.ResultApplied || trial.Step != TrialStep.ConfirmCriminalRecord ||
                !OwnsJurySeat(juryMember, trial, juryId, out var jury) || jury.ConfirmTestimony)
                return;

            jury.ConfirmTestimony = true;
            var occupiedSeats = trial.Jury.Values.Where(seat => seat.JuryMember != null).ToList();
            trial.SendPackets(new SCChangeJuryOKCountPacket(
                occupiedSeats.Count(seat => seat.ConfirmTestimony), occupiedSeats.Count));
            if (trial.AllJuryConfirmedCriminalRecords())
                trial.CurrentStepEndTime = DateTime.UtcNow;
        }
    }

    public void JuryVerdict(Character juryMember, uint trialId, int juryId, byte sentence)
    {
        var trial = GetTrial(trialId);
        if (trial == null)
            return;

        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || trial.ResultApplied || trial.Step != TrialStep.JuryVerdict ||
                sentence is < 1 or > 6 ||
                !OwnsJurySeat(juryMember, trial, juryId, out var jury) || jury.SelectedSentence >= 0)
                return;

            jury.SelectedSentence = sentence;
            var occupiedSeats = trial.Jury.Values.Where(seat => seat.JuryMember != null).ToList();
            var count = occupiedSeats.Count(seat => seat.SelectedSentence >= 0);
            var total = occupiedSeats.Count;

            trial.SendPackets(new SCChangeJuryVerdictCountPacket(count, total), true);
            var resultPacket = new SCRulingStatusPacket(count, total, TrialSentenceResult.Undefined, 0);
            trial.Defendant?.SendPacket(resultPacket);
            foreach (var seat in occupiedSeats.Where(seat => seat.SelectedSentence >= 0))
                seat.JuryMember.SendPacket(resultPacket);

            if (trial.AllJurySelectedSentence())
                trial.CurrentStepEndTime = DateTime.UtcNow;
        }
    }

    private bool IsCurrentTrial(TrialData trial)
    {
        return Trials.TryGetValue(trial.Id, out var current) && ReferenceEquals(current, trial);
    }

    private static bool OwnsJurySeat(Character player, TrialData trial, int seatId, out TrialJuryBox jury)
    {
        jury = null;
        return player != null && trial.Jury.TryGetValue(seatId, out jury) &&
               jury.SeatId == seatId && ReferenceEquals(jury.JuryMember, player);
    }

    public void CancelTrial(Character defendant, uint trialId)
    {
        var trial = GetTrial(trialId);
        if (trial == null)
            return;

        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || trial.Defendant != defendant || trial.ResultApplied ||
                trial.Step is not (TrialStep.DefendantAwaitingTrial or TrialStep.AwaitingJurySummons) ||
                trial.Jury.Values.Any(seat => seat.JuryMember != null))
                return;

            trial.DecisionReceived = true;
            ResultIsGuilty(defendant, trial, true);
            NotifyTrialCancellation(trial);
        }
    }

    // Defendant declined a closing statement.
    public void SkipFinalStatementReply(Character defendant, uint trialId)
    {
        var trial = GetTrial(trialId);
        if (trial == null)
            return;
        lock (SaveManager.PersistenceSyncRoot)
        lock (trial.SyncRoot)
        {
            if (!IsCurrentTrial(trial) || trial.Defendant != defendant || trial.ResultApplied ||
                trial.Step != TrialStep.ClosingStatement)
                return;
            trial.CurrentStepEndTime = DateTime.UtcNow;
        }
    }

    public bool IsPlayerInCourt(uint playerId)
    {
        foreach (var trial in Trials.Values)
        {
            lock (trial.SyncRoot)
            {
                if (trial.DefendantId == playerId || trial.Jury.Values.Any(seat => seat.JuryMember?.Id == playerId))
                    return true;
            }
        }
        return false;
    }

    public void JoinTrialAudience(Character player, uint doodadTemplateId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (player == null || !player.IsOnline)
                return;
            foreach (var courtRoom in CourtRooms.Values)
            {
                if (!courtRoom.AudienceSeats.Any(seat => seat.TemplateId == doodadTemplateId && IsAtAudienceSeat(player, seat)))
                    continue;
                lock (courtRoom.AudienceMembers)
                    if (courtRoom.AudienceMembers.Contains(player))
                        return;
                LeaveTrialAudience(player);
                lock (courtRoom.AudienceMembers)
                    courtRoom.AudienceMembers.Add(player);
                player.BroadcastPacket(new SCTrialAudienceJoinedPacket(courtRoom.CurrentTrial?.Id ?? 0, player.ObjId, player.Name), true);
                courtRoom.TrialChatChannel?.JoinChannel(player);
                return;
            }
            Logger.Warn($"{player.Name} tried to join courtroom audience while not being near a seat");
        }
    }

    public void LeaveTrialAudience(Character player)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            foreach (var courtRoom in CourtRooms.Values)
            {
                bool removed;
                lock (courtRoom.AudienceMembers)
                    removed = courtRoom.AudienceMembers.Remove(player);
                if (!removed)
                    continue;
                player.BroadcastPacket(new SCTrialAudienceLeftPacket(player.ObjId, player.Name), true);
                courtRoom.TrialChatChannel?.LeaveChannel(player);
            }
        }
    }

    private static bool IsAtAudienceSeat(Character player, Models.Game.DoodadObj.Doodad seat)
    {
        return player.Transform.InstanceId == seat.Transform.InstanceId && player.GetDistanceTo(seat) <= 5f;
    }

    public Character[] GetTrialAudienceSnapshot(TrialData trial)
    {
        lock (trial.SyncRoot)
        {
            var room = trial.CourtRoom;
            if (room?.CurrentTrial != trial)
                return [];
            lock (room.AudienceMembers)
                return room.AudienceMembers.Where(player => player.IsOnline &&
                    room.AudienceSeats.Any(seat => IsAtAudienceSeat(player, seat))).ToArray();
        }
    }

    private static bool IsTrialSpeaker(TrialData trial, Character player)
    {
        if (player?.IsOnline != true || trial.CourtRoom?.CurrentTrial != trial)
            return false;
        if (trial.Defendant == player)
            return true;
        return trial.Jury.Values.Any(box => box.JuryMember == player && box.Seat != null &&
            player.Transform.InstanceId == box.Seat.Transform.InstanceId);
    }

    internal Character[] GetTrialChatRecipients(TrialData trial, Character sender)
    {
        lock (trial.SyncRoot)
        {
            if (trial.Step < TrialStep.VerifyCriminalRecord || trial.Step > TrialStep.JuryVerdict || !IsTrialSpeaker(trial, sender))
                return [];
            return trial.Jury.Values.Select(box => box.JuryMember).Append(trial.Defendant)
                .Where(player => IsTrialSpeaker(trial, player))
                .Concat(GetTrialAudienceSnapshot(trial)).Distinct().ToArray();
        }
    }

    public int SendTrialChat(Character sender, string message, int ability, byte languageType)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var trial = GetParticipatingTrial(sender);
            if (trial != null)
            {
                lock (trial.SyncRoot)
                {
                    var recipients = GetTrialChatRecipients(trial, sender);
                    if (recipients.Length > 0)
                    {
                        foreach (var recipient in recipients)
                            recipient.SendPacket(new SCChatMessagePacket(ChatType.Judge, sender, message, ability, languageType));
                        return recipients.Length;
                    }
                }
            }
            sender.SendErrorMessage(ErrorMessageType.ChatNotInTrial);
            return 0;
        }
    }

    public TrialData GetParticipatingTrial(Character player)
    {
        if (player == null)
            return null;
        foreach (var courtRoom in CourtRooms.Values)
        {
            var trial = courtRoom.CurrentTrial;
            if (trial == null)
                continue;
            lock (trial.SyncRoot)
            {
                if (courtRoom.CurrentTrial != trial)
                    continue;
                if (trial.Defendant == player || trial.Jury.Values.Any(box => box.JuryMember == player))
                    return trial;
                if (GetTrialAudienceSnapshot(trial).Contains(player))
                    return trial;
            }
        }
        return null;
    }
}
