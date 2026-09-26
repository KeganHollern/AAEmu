using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Duels;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Tasks.Duels;
using NLog;

namespace AAEmu.Game.Core.Managers;

public class DuelManager : Singleton<DuelManager>, IDuelManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private readonly object _lock = new();
    private readonly Dictionary<uint, Duel> _duels = [];
    private const float DistanceForSurrender = 75;
    private static readonly TimeSpan CheckDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DuelDuration = TimeSpan.FromMinutes(5);

    public void Initialize() => Logger.Info("Initialising Duel Manager...");

    private bool IsCurrentCore(Duel duel) => duel != null &&
        _duels.GetValueOrDefault(duel.Challenger.Id) == duel &&
        _duels.GetValueOrDefault(duel.Challenged.Id) == duel;

    internal bool IsCurrent(Duel duel)
    {
        lock (_lock)
            return IsCurrentCore(duel);
    }

    internal bool AreActiveOpponents(Character first, Character second)
    {
        lock (_lock)
            return first != null && second != null && first != second &&
                   _duels.TryGetValue(first.Id, out var duel) && duel.Active &&
                   (duel.Challenger == first && duel.Challenged == second ||
                    duel.Challenged == first && duel.Challenger == second);
    }

    internal bool AreActiveOpponents(BaseUnit first, BaseUnit second) =>
        first is Character or Mate && second is Character or Mate &&
        AreActiveOpponents(first.GetOwnerCharacter(), second.GetOwnerCharacter());

    internal bool IsNonlethalOpponentDamage(BaseUnit attacker, Character victim)
    {
        if (attacker is not (Character or Mate))
            return false;
        var owner = attacker.GetOwnerCharacter();
        lock (_lock)
            return owner != null && owner != victim && _duels.TryGetValue(victim.Id, out var duel) &&
                   (duel.Active || duel.Ending) &&
                   (duel.Challenger == victim && duel.Challenged == owner ||
                    duel.Challenged == victim && duel.Challenger == owner);
    }

    internal Duel GetActiveDuel(BaseUnit caster)
    {
        var owner = caster is Character or Mate ? caster.GetOwnerCharacter() : null;
        lock (_lock)
            return owner != null && _duels.TryGetValue(owner.Id, out var duel) && duel.Active ? duel : null;
    }

    internal EffectLease EnterEffect(BaseUnit caster, BaseUnit target, EffectSource source)
    {
        var casterOwner = caster is Character or Mate ? caster.GetOwnerCharacter() : null;
        var targetOwner = target is Character or Mate ? target.GetOwnerCharacter() : null;
        lock (_lock)
        {
            var duel = source?.DuelContext ?? source?.Skill?.DuelContext;
            var current = casterOwner == null ? null : _duels.GetValueOrDefault(casterOwner.Id);
            var targetsCurrentOpponent = current is { Active: true } && casterOwner != targetOwner &&
                (current.Challenger == casterOwner && current.Challenged == targetOwner ||
                 current.Challenged == casterOwner && current.Challenger == targetOwner);
            // A cast from an old duel cannot adopt a new opponent's duel.
            // Unrelated NPC and bystander effects retain their ordinary behavior.
            if (targetsCurrentOpponent && duel != null && duel != current)
                return new EffectLease(null, null, false);
            if (duel == null && targetsCurrentOpponent)
                duel = current;
            if (duel == null || casterOwner == null || casterOwner == targetOwner ||
                !(duel.Challenger == casterOwner && duel.Challenged == targetOwner ||
                  duel.Challenged == casterOwner && duel.Challenger == targetOwner))
                return new EffectLease(null, null, true);
            if (source != null)
            {
                source.DuelContext = duel;
                if (source.Skill != null)
                    source.Skill.DuelContext ??= duel;
            }
            if (!IsCurrentCore(duel) || !duel.Active || duel.Ending || duel.ResultPending)
                return new EffectLease(null, null, false);
            duel.EffectsInFlight++;
            return new EffectLease(this, duel, true);
        }
    }

    internal sealed class EffectLease(DuelManager manager, Duel duel, bool allowed) : IDisposable
    {
        private int _disposed;
        public bool Allowed { get; } = allowed;

        public void Dispose()
        {
            if (manager != null && Interlocked.Exchange(ref _disposed, 1) == 0)
                manager.CompleteEffect(duel);
        }
    }

    private void CompleteEffect(Duel duel)
    {
        DuelDetType result;
        uint loser;
        lock (_lock)
        {
            duel.EffectsInFlight--;
            if (duel.EffectsInFlight != 0 || !duel.ResultPending)
                return;
            result = duel.PendingResult;
            loser = duel.PendingLoserId;
        }
        Stop(duel, result, loser);
    }

    // Called under the manager lock at request, acceptance, and countdown completion.
    internal static ErrorMessageType ValidateParticipants(Character challenger, Character challenged, float requestRange)
    {
        if (challenger == null || challenged == null || !challenger.IsOnline || !challenged.IsOnline ||
            challenger.Hp <= 0 || challenged.Hp <= 0)
            return ErrorMessageType.BadDuelTarget;
        if (challenger == challenged || challenger.Id == challenged.Id)
            return ErrorMessageType.BadDuelSelf;
        if (challenger.ParentWorld == null || challenger.ParentWorld != challenged.ParentWorld ||
            challenger.Transform.InstanceId != challenged.Transform.InstanceId)
            return ErrorMessageType.DuelBadLocation;
        var distance = Vector3.DistanceSquared(challenger.Transform.World.Position, challenged.Transform.World.Position);
        if (!float.IsFinite(distance) || distance > requestRange * requestRange)
            return ErrorMessageType.DuelBadLocation;
        if (challenger.ForceAttack)
            return ErrorMessageType.CannotDuelDuringForceAttack;
        if (challenged.ForceAttack)
            return ErrorMessageType.CannotDuelDuringForceAttackOther;
        return 0;
    }

    internal bool TryCreateDuel(Character challenger, Character challenged, float requestRange, out Duel duel)
    {
        lock (_lock)
        {
            duel = null;
            var error = ValidateParticipants(challenger, challenged, requestRange);
            if (error == 0 && (_duels.ContainsKey(challenger.Id) || challenger.IsInDuel))
                error = ErrorMessageType.AlreadyInDuel;
            if (error == 0 && (_duels.ContainsKey(challenged.Id) || challenged.IsInDuel))
                error = ErrorMessageType.OtherAlreadyInDuel;
            if (error != 0)
            {
                challenger?.SendErrorMessage(error);
                return false;
            }
            duel = new Duel(challenger, challenged) { RequestRange = requestRange };
            _duels.Add(challenger.Id, duel);
            _duels.Add(challenged.Id, duel);
            challenged.SendPacket(new SCDuelChallengedPacket(challenger.Id));
            return true;
        }
    }

    public void DuelRequest(Character challenger, uint challengedId)
    {
        TryCreateDuel(challenger, WorldManager.Instance.GetCharacterById(challengedId),
            Duel.RequestRangeMeters, out _);
    }

    internal bool TryAccept(Character sender, uint challengerId, out Duel duel)
    {
        lock (_lock)
        {
            duel = _duels.GetValueOrDefault(challengerId);
            if (duel == null || duel.Challenger.Id != challengerId || duel.Challenged != sender || duel.DuelStarted)
                return false;
            var error = ValidateParticipants(duel.Challenger, duel.Challenged, duel.RequestRange);
            if (error != 0)
            {
                duel.Challenger.SendErrorMessage(error);
                Remove(duel);
                return false;
            }
            duel.DuelStarted = true;
            return true;
        }
    }

    public void DuelAccepted(Character challenged, uint challengerId)
    {
        if (!TryAccept(challenged, challengerId, out var duel))
            return;
        CreateFlag(duel, () =>
        {
            var flagPosition = duel.Challenger.Transform.CloneAsSpawnPosition();
            var midpoint = (duel.Challenger.Transform.World.Position + challenged.Transform.World.Position) / 2;
            var policy = duel.Challenger.Transform.Parent != null || duel.Challenger.Transform.StickyParent != null ||
                         challenged.Transform.Parent != null || challenged.Transform.StickyParent != null
                ? DynamicDoodadPlacementPolicy.PreserveParentedHeight
                : DynamicDoodadPlacementPolicy.GroundToNearbySurface;
            if (!DynamicDoodadPlacement.TryResolve(challenged.ParentWorld.Template.GeoData, midpoint, policy, out var resolved))
                return null;
            flagPosition.X = resolved.X;
            flagPosition.Y = resolved.Y;
            flagPosition.Z = resolved.Z;
            return new DoodadSpawner { ParentWorld = challenged.ParentWorld, UnitId = 5014, Position = flagPosition }.Spawn(0);
        }, () =>
        {
            duel.SendPacketsBoth(new SCAreaChatBubblePacket(true, duel.Challenger.ObjId, 543));
            duel.SendPacketsBoth(new SCDuelStatePacket(duel.Challenger.ObjId, duel.DuelFlag.ObjId));
            duel.SendPacketsBoth(new SCDuelStatePacket(challenged.ObjId, duel.DuelFlag.ObjId));
            duel.SendPacketChallenger(new SCDoodadPhaseChangedPacket(duel.DuelFlag));
            duel.SendPacketsBoth(new SCDuelStartCountdownPacket());
            duel.DuelStartTask = new DuelStartTask(duel);
            TaskManager.Instance.Schedule(duel.DuelStartTask, TimeSpan.FromSeconds(3));
        });
    }

    internal void CreateFlag(Duel duel, Func<Doodad> createFlag, Action publishCountdown)
    {
        // Doodad spawn takes the persistence lock. It must not run under the
        // duel lock: item effects can acquire those locks in the reverse order.
        Doodad flag;
        try
        {
            flag = createFlag();
        }
        catch
        {
            Stop(duel, DuelDetType.Draw);
            throw;
        }
        var accepted = false;
        lock (_lock)
        {
            if (flag != null && IsCurrentCore(duel) && !duel.Ending &&
                flag.ParentWorld == duel.Challenged.ParentWorld &&
                flag.Transform.InstanceId == duel.Challenged.Transform.InstanceId &&
                ValidateParticipants(duel.Challenger, duel.Challenged, duel.RequestRange) == 0)
            {
                duel.DuelFlag = flag;
                duel.Challenger.IsInDuel = true;
                duel.Challenged.IsInDuel = true;
                accepted = true;
                publishCountdown();
            }
        }
        if (!accepted)
        {
            flag?.Delete();
            Stop(duel, DuelDetType.Draw);
        }
    }

    public void DuelCancel(Character sender, uint challengerId, ErrorMessageType errorMessage)
    {
        lock (_lock)
        {
            var duel = _duels.GetValueOrDefault(challengerId);
            if (duel == null || duel.Challenger.Id != challengerId || duel.Challenged != sender || duel.DuelStarted ||
                errorMessage != ErrorMessageType.TargetRejectedDuel)
                return;
            duel.Challenger.SendErrorMessage(ErrorMessageType.TargetRejectedDuel);
            Remove(duel);
        }
    }

    public void DuelStart(Duel duel)
    {
        var invalid = false;
        lock (_lock)
        {
            if (!IsCurrentCore(duel) || !duel.DuelStarted || duel.Active || duel.Ending)
                return;
            invalid = ValidateParticipants(duel.Challenger, duel.Challenged, duel.RequestRange) != 0;
            if (!invalid)
            {
                duel.Active = true;
                duel.DuelStartTask = null;
                duel.SendPacketsBoth(new SCDuelStartedPacket(duel.Challenger.ObjId, duel.Challenged.ObjId));
                duel.SendPacketsBoth(new SCCombatEngagedPacket(duel.Challenger.ObjId));
                duel.SendPacketsBoth(new SCCombatEngagedPacket(duel.Challenged.ObjId));
                duel.DuelEndTimerTask = new DuelEndTimerTask(duel);
                TaskManager.Instance.Schedule(duel.DuelEndTimerTask, DuelDuration);
            }
        }
        if (invalid)
            Stop(duel, DuelDetType.Draw);
        else
            CheckDistance(duel);
    }

    internal void CompleteDamage(Character victim, BaseUnit attacker)
    {
        Duel duel;
        lock (_lock)
        {
            if (victim.Hp != 1 || !AreActiveOpponents(attacker, victim))
                return;
            duel = _duels.GetValueOrDefault(victim.Id);
        }
        Stop(duel, DuelDetType.Win, victim.Id);
    }

    public void CancelForCharacter(Character character)
    {
        Duel duel;
        lock (_lock)
            duel = character == null ? null : _duels.GetValueOrDefault(character.Id);
        Stop(duel, DuelDetType.Draw);
    }

    internal void Stop(Duel duel, DuelDetType result, uint loserId = 0)
    {
        bool wasActive;
        lock (_lock)
        {
            if (!IsCurrentCore(duel) || duel.Ending)
                return;
            // An effect can finish damage, crime checks and buff callbacks without
            // a manager lock. Keep the relation until those effects return, then
            // remove their hostile buffs. New effects reject the reserved result.
            if (duel.EffectsInFlight > 0)
            {
                if (!duel.ResultPending || result == DuelDetType.Draw)
                {
                    duel.PendingResult = result;
                    duel.PendingLoserId = loserId;
                }
                duel.ResultPending = true;
                return;
            }
            wasActive = duel.Active;
            duel.Active = false;
            duel.Ending = true;
        }
        try
        {
            if (duel.DuelStarted)
            {
                if (result == DuelDetType.Draw)
                {
                    duel.SendPacketChallenged(new SCDuelEndedPacket(duel.Challenger.Id, duel.Challenged.Id,
                        duel.Challenger.ObjId, duel.Challenged.ObjId, result));
                    duel.SendPacketChallenger(new SCDuelEndedPacket(duel.Challenged.Id, duel.Challenger.Id,
                        duel.Challenged.ObjId, duel.Challenger.ObjId, result));
                }
                else if (loserId == duel.Challenger.Id || loserId == duel.Challenged.Id)
                {
                    var loser = loserId == duel.Challenger.Id ? duel.Challenger : duel.Challenged;
                    var winner = loser == duel.Challenger ? duel.Challenged : duel.Challenger;
                    duel.SendPacketsBoth(new SCDuelEndedPacket(winner.Id, loser.Id, winner.ObjId, loser.ObjId, result));
                    if (wasActive)
                        winner.Achievements.Increment(CharRecordKind.WinDuel, 0, 0);
                }
                duel.SendPacketsBoth(new SCDuelStatePacket(duel.Challenger.ObjId, 0));
                duel.SendPacketsBoth(new SCDuelStatePacket(duel.Challenged.ObjId, 0));
                if (duel.DuelFlag != null)
                {
                    duel.DuelFlag.Delete();
                    duel.SendPacketsBoth(new SCDoodadRemovedPacket(duel.DuelFlag.ObjId));
                    duel.DuelFlag = null;
                }
                RemoveHostileEffects(duel.Challenger, duel.Challenged);
                RemoveHostileEffects(duel.Challenged, duel.Challenger);
            }
        }
        finally
        {
            lock (_lock)
                Remove(duel);
        }
    }

    private static void RemoveHostileEffects(Character target, Character opponent)
    {
        target.Buffs.RemoveBadBuffsFromCaster(opponent.ObjId);
        List<Buff> good = [], bad = [], hidden = [];
        target.Buffs.GetAllBuffs(good, bad, hidden, false);
        foreach (var buff in bad.Concat(hidden))
            if (buff.Template is { System: false, Kind: BuffKind.Bad } &&
                buff.Caster is Mate && buff.Caster.GetOwnerCharacter() == opponent)
                buff.Exit();
    }

    private void Remove(Duel duel)
    {
        if (!IsCurrentCore(duel))
            return;
        duel.Active = false;
        duel.Challenger.IsInDuel = false;
        duel.Challenged.IsInDuel = false;
        if (duel.DuelStartTask != null) _ = duel.DuelStartTask.Cancel();
        if (duel.DuelEndTimerTask != null) _ = duel.DuelEndTimerTask.Cancel();
        if (duel.DuelDistanceСheckTask != null) _ = duel.DuelDistanceСheckTask.Cancel();
        duel.DuelStartTask = null;
        duel.DuelEndTimerTask = null;
        duel.DuelDistanceСheckTask = null;
        _duels.Remove(duel.Challenger.Id);
        _duels.Remove(duel.Challenged.Id);
    }

    internal void EndTimer(Duel duel)
    {
        uint loser;
        DuelDetType result;
        lock (_lock)
        {
            if (!IsCurrentCore(duel) || !duel.Active)
                return;
            loser = duel.Challenger.Hp < duel.Challenged.Hp ? duel.Challenger.Id : duel.Challenged.Id;
            result = duel.Challenger.Hp == duel.Challenged.Hp ? DuelDetType.Draw : DuelDetType.Win;
        }
        Stop(duel, result, loser);
    }

    internal void CheckDistance(Duel duel)
    {
        var stop = false;
        var result = DuelDetType.Draw;
        var loser = 0u;
        lock (_lock)
        {
            if (!IsCurrentCore(duel) || !duel.Active)
                return;
            foreach (var player in new[] { duel.Challenger, duel.Challenged })
            {
                if (!player.IsOnline || player.Hp <= 0)
                {
                    stop = true;
                    break;
                }
                var distance = Vector3.DistanceSquared(duel.DuelFlag.Transform.World.Position, player.Transform.World.Position);
                if (player.ParentWorld != duel.DuelFlag.ParentWorld || !float.IsFinite(distance) ||
                    distance >= DistanceForSurrender * DistanceForSurrender)
                {
                    stop = true;
                    result = DuelDetType.Surrender;
                    loser = player.Id;
                    break;
                }
            }
            if (!stop)
            {
                duel.DuelDistanceСheckTask = new DuelDistanceСheckTask(duel);
                TaskManager.Instance.Schedule(duel.DuelDistanceСheckTask, CheckDelay);
            }
        }
        if (stop)
            Stop(duel, result, loser);
    }
}
