using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Models.Game.InstantGame;

public partial class InstantGame
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    private readonly List<Character> _players;
    private readonly Dictionary<uint, List<Character>> _corps;
    private readonly Dictionary<Character, InstantCorps> _characterCorps;

    private readonly InstantGameTeamResult _corps1Result;
    private readonly InstantGameTeamResult _corps2Result;
    private readonly Dictionary<Character, InstantGameTeamMember> _members;

    private readonly WorldInstance _world;
    private readonly Battlefield _battlefield;
    private readonly ZoneInstanceId _zoneInstanceId;

    private readonly CancellationTokenSource _endGameTokenSource;

    private static readonly TimeSpan StartCountdown = TimeSpan.FromSeconds(20);
    private readonly TimeProvider _clock;
    private long? _announcedAt;
    private bool _finished;
    private bool _entryStarted;
    private bool _participantLeftDuringCountdown;
    internal Func<ArenaMatchResult, Task> SaveMatchResult { get; init; } = result => InstantGameManager.Instance.RecordMatchResult(result);

    public InstantGame(Battlefield battlefield) : this(battlefield,
        WorldManager.Instance.CreateWorldInstance(WorldManager.Instance.GetWorldTemplateByZoneKey(battlefield.ZoneKey), 0),
        TimeProvider.System)
    {
    }

    internal InstantGame(Battlefield battlefield, WorldInstance world, TimeProvider clock)
    {
        _clock = clock;
        _world = world;
        _battlefield = battlefield;
        _players = [];

        _members = new Dictionary<Character, InstantGameTeamMember>();
        _corps1Result = new InstantGameTeamResult(VictoryState.Invalid, _battlefield.RuleSet.Corps1FactionId);
        _corps2Result = new InstantGameTeamResult(VictoryState.Invalid, _battlefield.RuleSet.Corps2FactionId);

        _corps = new Dictionary<uint, List<Character>>
        {
            {_battlefield.RuleSet.Corps1FactionId, [] },
            {_battlefield.RuleSet.Corps2FactionId, [] }
        };

        _characterCorps = new Dictionary<Character, InstantCorps>();

        _zoneInstanceId = new ZoneInstanceId(_battlefield.ZoneKey, _world.Id);

        _endGameTokenSource = new CancellationTokenSource();
    }

    public void AddPlayer(Character character, InstantCorps corps)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!PrisonerAccess.CanEnter(character, true))
                return;
            if (_players.Contains(character))
            {
                // Player already exists in game, remove for correction
                RemovePlayer(character);
            }
            _players.Add(character);
            var factionId = corps == InstantCorps.Corps1 ? _battlefield.RuleSet.Corps1FactionId : _battlefield.RuleSet.Corps2FactionId;
            _corps[factionId].Add(character);
            _characterCorps.Add(character, corps);

            character.SendPacket(new SCInviteToInstantGamePacket(_zoneInstanceId, _battlefield.RuleSet.Id, corps, 1));
            character.CurrentInstantGame = this;
        }
    }

    public bool RemovePlayer(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (character == null)
                return false;

            if (!_players.Contains(character))
                return false;

            if (_announcedAt is { } start && _members.ContainsKey(character) && _clock.GetElapsedTime(start) < StartCountdown)
                _participantLeftDuringCountdown = true;
            _players.Remove(character);

            foreach (var members in _corps.Values)
                members.Remove(character);

            _characterCorps.Remove(character);
            if (character.CurrentInstantGame == this)
                character.CurrentInstantGame = null;
            return true;
        }
    }

    internal void DisconnectPlayer(Character character)
    {
        if (IsInvited(character))
        {
            CancelAdmission(character);
            return;
        }

        if (!RemovePlayer(character))
            return;

        RemoveDepartedMember(character);
        RefillOrReleaseBeforeStart();
    }

    private void RemoveDepartedMember(Character character)
    {
        character.Events.OnKill -= OnKill;
        if (_members.Remove(character, out var member))
        {
            character.SetFaction(character.OriginFaction.Id);
            // A player earns a place in the results only after the match starts.
            if (!_announcedAt.HasValue && !_finished)
                member.Corps.Members.Remove(member);
        }
    }

    private void RefillOrReleaseBeforeStart()
    {
        if (_finished || _announcedAt.HasValue)
            return;

        // Keep the world while a player is loading or still has an invitation.
        _entryStarted = _players.Any(player => !IsInvited(player));
        InstantGameManager.Instance.OnInvitationRemoved(this);
    }

    private bool IsInvited(Character character) =>
        character.CurrentInstantGame == this && !_members.ContainsKey(character) &&
        (!_entryStarted || character.Transform.InstanceId != _world.Id);

    internal void CancelAdmission(Character character)
    {
        var invited = IsInvited(character);
        var removed = RemovePlayer(character);
        if (character.CurrentInstantGame == this)
            character.CurrentInstantGame = null;
        if (_members.Remove(character, out var member))
        {
            if (!_finished)
                member.Corps?.Members.Remove(member);
            character.SetFaction(character.OriginFaction.Id);
        }
        character.Events.OnKill -= OnKill;
        if (removed)
        {
            if (invited)
                InstantGameManager.Instance.OnInvitationRemoved(this);
            else
                RefillOrReleaseBeforeStart();
        }
    }

    internal bool HasPlayer(uint characterId) => _players.Any(player => player.Id == characterId);

    internal bool IsFinished => _finished;

    // Each match owns its invitation ID object, even when a world instance number is reused.
    internal bool CanInvite(MatchmakingApplicant applicant) => !ReferenceEquals(applicant.DeclinedRoom, _zoneInstanceId);

    public bool IsFull => _players.Count == _battlefield.RuleSet.CorpsSize * 2;

    public uint BattlefieldId => _battlefield.Id;

    public InstantCorps GetCorps()
    {
        if (_battlefield.Id == (uint)InstantGameType.Gladiator)
        {
            if (!_characterCorps.ContainsValue(InstantCorps.Corps1))
                return InstantCorps.Corps1;
            if (!_characterCorps.ContainsValue(InstantCorps.Corps2))
                return InstantCorps.Corps2;
            return InstantCorps.Invalid;
        }

        var a = _characterCorps.Count(o => o.Value == InstantCorps.Corps1);
        var b = _characterCorps.Count(o => o.Value == InstantCorps.Corps2);
        return b > a ? InstantCorps.Corps1 : InstantCorps.Corps2;

    }

    public void PlayerInviteResponse(Character character, bool joins, ulong qualifierId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!PrisonerAccess.CanEnter(character, true))
            {
                CancelAdmission(character);
                return;
            }
            if (!IsInvited(character) || !_characterCorps.TryGetValue(character, out var corps))
                return;
            if (!joins)
            {
                // Next Room keeps the application open for a different match.
                InstantGameManager.Instance.WithdrawFromBattlefield(character);
                InstantGameManager.Instance.ApplyToBattlefield(_battlefield.Id, InstantCorps.Any, character, _zoneInstanceId);
                return;
            }

            if (!InstantGameManager.PlayerCanEnter(character) || !_battlefield.CheckEntryLevel(character))
            {
                CancelAdmission(character);
                return;
            }
            // A trial owns the participant's return position until the court releases them.
            if (TrialManager.Instance.IsPlayerInCourt(character.Id))
            {
                character.SendErrorMessage(ErrorMessageType.CannotUsePortalInTrial);
                CancelAdmission(character);
                return;
            }
            var spawn = corps == InstantCorps.Corps1 ? _battlefield.Spawns.Corps1Spawn : _battlefield.Spawns.Corps2Spawn;
            _entryStarted = true;
            MoveCharacterToWorld(character, _battlefield.ZoneKey, spawn.X, spawn.Y, spawn.Z);
        }
    }

    public void OnEnterWorld(Character character, ulong qualifierId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!PrisonerAccess.CanEnter(character, true))
            {
                // A sentence issued during the load owns the new jail position. Do not
                // restore the battlefield's older pre-arrest MainWorldPosition here.
                CancelAdmission(character);
                return;
            }
            if (character.CurrentInstantGame != this || !_characterCorps.TryGetValue(character, out var corps))
                return;
            _entryStarted = true;
            character.SendPacket(new SCInstantGameJoinedPacket(_zoneInstanceId, corps, _battlefield.RuleSet));

            if (corps == InstantCorps.Corps1)
                character.SetFaction((FactionsEnum)_battlefield.RuleSet.Corps1FactionId);
            else
                character.SetFaction((FactionsEnum)_battlefield.RuleSet.Corps2FactionId);

            character.Events.OnKill += OnKill;

            var member = new InstantGameTeamMember { Character = character };
            _members.Add(character, member);

            var result = corps == InstantCorps.Corps1 ? _corps1Result : _corps2Result;
            result.Members.Add(member);
            member.Corps = result;

            // TODO: This can be done better.
            // TODO: Game expire after 60 seconds if not enough players
            if (_members.Count == _battlefield.RuleSet.CorpsSize * 2)
                Start();
        }
    }

    private TimeSpan PlayingTime => TimeSpan.FromMinutes(_battlefield.RuleSet.TimePlaying);

    public void Start()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_announcedAt.HasValue || _finished)
                return;

            _announcedAt = _clock.GetTimestamp();
            var now = (uint)(Environment.TickCount & int.MaxValue);
            BroadcastPacket(new SCInstantGameStartPacket(_zoneInstanceId, now + (uint)StartCountdown.TotalMilliseconds, now));
            _ = ResetPlayersAfterStart();
            _ = EndOnTimeout();
        }
    }

    private async Task ResetPlayersAfterStart()
    {
        await Task.Delay(TimeSpan.FromSeconds(3), _clock);
        ResetPlayers();
    }

    private async Task EndOnTimeout()
    {
        await Task.Delay(StartCountdown + PlayingTime, _clock, _endGameTokenSource.Token);
        await EndGame();
    }

    internal void ResetPlayers()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_finished)
                return;
            foreach (var (character, _) in _characterCorps.ToArray())
            {
                if (character == null || character.CurrentInstantGame != this)
                {
                    continue;
                }

                if (!PrisonerAccess.CanEnter(character, true))
                {
                    CancelAdmission(character);
                    continue;
                }

                // Reset HP and MP
                // Reset HP
                character.Hp = character.MaxHp;
                character.Mp = character.MaxMp;
                character.BroadcastPacket(new SCUnitPointsPacket(character.ObjId, character.Hp, character.Mp), true);
                // Reset Buffs
                character.Buffs.RemoveAllEffects();
                // Reset Cooldowns
                character.ResetAllSkillCooldowns(false);
            }
        }
    }

    public Task EndGame() => FinishGame(BattlefieldEndingReason.Timeout);

    private async Task FinishGame(BattlefieldEndingReason reason, GamePacket finalPoints = null)
    {
        Task saved = Task.CompletedTask;
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_finished)
                return;
            _finished = true;
            InstantGameManager.Instance.StopRefill(this);
            _endGameTokenSource.Cancel();

            (_corps1Result.State, _corps2Result.State) = _corps1Result.Score.CompareTo(_corps2Result.Score) switch
            {
                > 0 => (VictoryState.Win, VictoryState.Lose),
                < 0 => (VictoryState.Lose, VictoryState.Win),
                _ => (VictoryState.Draw, VictoryState.Draw)
            };
            // Register this outcome before a disconnect or the final Game checkpoint can run.
            if (GetCompletedResult() is { } result)
                saved = SaveMatchResult(result);
        }

        await saved;
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (finalPoints != null)
                BroadcastPacket(finalPoints);
            BroadcastPacket(new SCInstantGameEndPacket(_zoneInstanceId, reason, _corps1Result, _corps2Result));
        }

        await Task.Delay(TimeSpan.FromMinutes(_battlefield.RuleSet.TimeEnding), _clock);
        lock (SaveManager.PersistenceSyncRoot)
            DestroyInstantGame();
    }

    private ArenaMatchResult GetCompletedResult()
    {
        if (_participantLeftDuringCountdown ||
            _announcedAt is not { } start ||
            _clock.GetElapsedTime(start) < StartCountdown || _members.Count == 0 ||
            _corps1Result.Members.Count == 0 || _corps2Result.Members.Count == 0)
            return null;

        var id = Guid.NewGuid();
        GladiatorRankResult rank = null;
        if (_battlefield.Id == (uint)InstantGameType.Gladiator)
        {
            if (_corps1Result.Members.Count != 1 || _corps2Result.Members.Count != 1 ||
                _corps1Result.Members[0].Character.Id == _corps2Result.Members[0].Character.Id)
                return null;
            rank = GetGladiatorRank(id);
        }
        else if (_battlefield.Id != (uint)InstantGameType.DrillCamp)
            return null;

        var rule = _battlefield.RuleSet;
        var members = _corps1Result.Members.Concat(_corps2Result.Members).ToArray();
        var topPlayerKills = members.Max(member => member.Kills);
        var topNpcKills = members.Max(member => member.EnemyNpcKills);
        foreach (var member in members)
            member.SetMatchHonor(rule, topPlayerKills > 0 && member.Kills == topPlayerKills,
                topNpcKills > 0 && member.EnemyNpcKills == topNpcKills);
        var awards = members.Select(member => new ArenaHonorAward(member.Character.Id, member.Bonus)).ToArray();
        return new ArenaMatchResult(id, _battlefield.Id, rank, awards);
    }

    private GladiatorRankResult GetGladiatorRank(Guid id)
    {
        var red = _corps1Result.Members[0];
        var blue = _corps2Result.Members[0];

        var rule = _battlefield.RuleSet;
        var (redPoints, bluePoints) = _corps1Result.State switch
        {
            VictoryState.Win => (rule.RankWinPoints, rule.RankLossPoints),
            VictoryState.Lose => (rule.RankLossPoints, rule.RankWinPoints),
            VictoryState.Draw => (rule.RankDrawPoints, rule.RankDrawPoints),
            _ => throw new InvalidOperationException("Gladiator has no final team result.")
        };
        var now = _clock.GetUtcNow().UtcDateTime;
        return new GladiatorRankResult(id, now.AddTicks(-(now.Ticks % 10)),
            red.Character.Id, red.Score, redPoints, blue.Character.Id, blue.Score, bluePoints);
    }

    internal bool TryAbandonBeforeEntry()
    {
        if (_players.Count != 0 || _entryStarted || _announcedAt.HasValue || _finished)
            return false;

        _finished = true;
        DestroyInstantGame();
        return true;
    }

    private void DestroyInstantGame()
    {
        foreach (var character in _players.ToList())
        {
            LeaveInstantGame(character);
        }

        WorldManager.Instance.RemoveArenaWorld(_world);
        InstantGameManager.Instance.RemoveGame(this);
    }

    public void LeaveInstantGame(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (character.CurrentInstantGame != this || !_players.Contains(character))
                return;
            if (character.IsPrisoner)
            {
                CancelAdmission(character);
                return;
            }

            RemovePlayer(character);
            RemoveDepartedMember(character);
            if (character.MainWorldPosition == null)
            {
                _log.Warn($"Character {character.Name} ({character.Id}) does not have MainWorldPosition when leaving instant game!");
                return;
            }

            LeaveWorldForTransfer(character);
            character.Transform = character.MainWorldPosition.Clone();
            character.Transform.InstanceId = WorldManager.DefaultInstanceId;
            character.ParentWorld?.AddObject(character);
            character.SendPacket(
                new SCLoadInstancePacket(
                    WorldManager.DefaultInstanceId,
                    character.MainWorldPosition.ZoneId,
                    character.MainWorldPosition.World.Position.X,
                    character.MainWorldPosition.World.Position.Y,
                    character.MainWorldPosition.World.Position.Z,
                    character.MainWorldPosition.World.Rotation.X.DegToRad(),
                    character.MainWorldPosition.World.Rotation.Y.DegToRad(),
                    character.MainWorldPosition.World.Rotation.Z.DegToRad()
                )
            );
            RefillOrReleaseBeforeStart();
        }
    }

    private void MoveCharacterToWorld(Character character, uint zoneId, float x, float y, float z)
    {
        if (character.Transform.InstanceId == WorldManager.DefaultInstanceId || character.MainWorldPosition == null)
            character.MainWorldPosition = character.Transform.CloneDetached(character);
        LeaveWorldForTransfer(character);
        character.Transform.ApplyWorldSpawnPosition(new WorldSpawnPosition { ZoneId = zoneId, X = x, Y = y, Z = z }, _world.Id);
        _world.AddObject(character);
        character.SendPacket(new SCLoadInstancePacket(_world.Id, zoneId, x, y, z, 0, 0, 0));
    }

    private static void LeaveWorldForTransfer(Character character)
    {
        character.DisabledSetPosition = true;
        // Attached objects stay in the old world when the player leaves its visible region.
        character.Transform.StickyParent = null;
        character.Transform.DetachAll();
        WorldManager.RemoveVisibleObject(character);
        character.ParentWorld?.RemoveObject(character);
    }

    public void BroadcastPacket(GamePacket packet)
    {
        foreach (var player in _players)
        {
            player.SendPacket(packet);
        }
    }
}
