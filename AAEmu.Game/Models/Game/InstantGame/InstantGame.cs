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

            _players.Remove(character);

            foreach (var members in _corps.Values)
                members.Remove(character);

            _characterCorps.Remove(character);
            if (character.CurrentInstantGame == this)
                character.CurrentInstantGame = null;
            return true;
        }
    }

    internal void CancelPendingInvitation(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (IsInvited(character))
                CancelAdmission(character);
        }
    }

    private bool IsInvited(Character character) =>
        character.CurrentInstantGame == this && !_members.ContainsKey(character) &&
        (!_entryStarted || character.Transform.InstanceId != _world.Id);

    internal void CancelAdmission(Character character)
    {
        var invited = IsInvited(character);
        if (RemovePlayer(character) && invited)
            InstantGameManager.Instance.OnInvitationRemoved(this);
        if (character.CurrentInstantGame == this)
            character.CurrentInstantGame = null;
        if (_members.Remove(character, out var member))
        {
            member.Corps?.Members.Remove(member);
            character.SetFaction(character.OriginFaction.Id);
        }
        character.Events.OnKill -= OnKill;
    }

    internal bool IsFinished => _finished;

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
            if (!joins)
            {
                // Next room, remove from current game then readd to requeue
                InstantGameManager.Instance.WithdrawFromBattlefield(character);
                InstantGameManager.Instance.ApplyToBattlefield(_battlefield.Id, InstantCorps.Any, character);
                return;
            }

            if (character.CurrentInstantGame != this || !_characterCorps.TryGetValue(character, out var corps))
                return;
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

    private async Task FinishGame(BattlefieldEndingReason reason)
    {
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
            BroadcastPacket(new SCInstantGameEndPacket(_zoneInstanceId, reason, _corps1Result, _corps2Result));
        }

        await Task.Delay(TimeSpan.FromMinutes(_battlefield.RuleSet.TimeEnding), _clock);
        lock (SaveManager.PersistenceSyncRoot)
            DestroyInstantGame();
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

        // TODO: Unbind all events from characters
        WorldManager.Instance.RemoveWorld(_world.Id);
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
            character.SetFaction(character.OriginFaction.Id);
            character.Events.OnKill -= OnKill;
            character.DisabledSetPosition = true;

            if (character.MainWorldPosition == null)
            {
                _log.Warn($"Character {character.Name} ({character.Id}) does not have MainWorldPosition when leaving instant game!");
                return;
            }

            character.Transform = character.MainWorldPosition.Clone();
            character.Transform.InstanceId = WorldManager.DefaultInstanceId;
            character.SendPacket(
                new SCLoadInstancePacket(
                    character.MainWorldPosition.WorldId,
                    character.MainWorldPosition.ZoneId,
                    character.MainWorldPosition.World.Position.X,
                    character.MainWorldPosition.World.Position.Y,
                    character.MainWorldPosition.World.Position.Z,
                    character.MainWorldPosition.World.Rotation.X.DegToRad(),
                    character.MainWorldPosition.World.Rotation.Y.DegToRad(),
                    character.MainWorldPosition.World.Rotation.Z.DegToRad()
                )
            );
        }
    }

    private void MoveCharacterToWorld(Character character, uint zoneId, float x, float y, float z)
    {
        character.DisabledSetPosition = true;
        character.MainWorldPosition ??= character.Transform.CloneDetached(character);
        character.Transform.ApplyWorldSpawnPosition(new WorldSpawnPosition { ZoneId = zoneId, X = x, Y = y, Z = z }, _world.Id);
        character.SendPacket(new SCLoadInstancePacket(_world.Id, zoneId, x, y, z, 0, 0, 0));
    }

    public void BroadcastPacket(GamePacket packet)
    {
        foreach (var player in _players)
        {
            player.SendPacket(packet);
        }
    }
}
