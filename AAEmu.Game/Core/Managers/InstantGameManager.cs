using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.InstantGame.Static;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class InstantGameManager : Singleton<InstantGameManager>, IInstantGameManager
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<uint, List<MatchmakingApplicant>> _matchmakingQueue = [];

    private readonly List<InstantGame> _instantGames = [];

    private readonly List<InstantGame> _queueList = [];

    private readonly Lock _lock = new();

    public void Initialize()
    {
        // 15 seconds between each matchmaking query
        TickManager.Instance.OnTick.Subscribe(BattlefieldTick, TimeSpan.FromSeconds(15));
    }

    public void ApplyToBattlefield(uint battlefieldId, InstantCorps corps, Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!PrisonerAccess.CanEnter(character, true))
                return;

            lock (_lock)
            {
                if (!_matchmakingQueue.TryGetValue(battlefieldId, out var applicants))
                {
                    if (BattlefieldGameData.Instance.GetBattlefield(battlefieldId)?.HasMatchData != true)
                    {
                        _log.Warn("[Matchmaking] Battlefield {0} has no complete match configuration.", battlefieldId);
                        return;
                    }
                    applicants = [];
                    _matchmakingQueue.Add(battlefieldId, applicants);
                }

                if (applicants.Any(applicant => applicant.CharObj == character))
                {
                    return;
                }

                applicants.Add(new MatchmakingApplicant(character));
            }

            _log.Trace("[Matchmaking] Added player " + character.Name + " to matchmaking queue for battlefield " +
                       battlefieldId);

            character.SendPacket(new SCAppliedToInstantGamePacket(battlefieldId, corps));
        }
    }

    internal void CancelAdmissions(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            lock (_lock)
            {
                foreach (var applicants in _matchmakingQueue.Values)
                    applicants?.RemoveAll(applicant => applicant.CharObj == character);
                foreach (var game in _instantGames.Concat(_queueList).Append(character.CurrentInstantGame).Distinct().ToArray())
                    game?.CancelAdmission(character);
            }
        }
    }

    public void WithdrawFromBattlefield(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            // Removes player from matchmaking if they cancel the queue.
            // Player not offline or invalid.

            if (character == null)
                return;

            lock (_lock)
            {
                foreach (var applicants in _matchmakingQueue.Values)
                {
                    if (applicants != null)
                    {
                        foreach (var player in applicants)
                        {
                            if (player.CharObj == character)
                            {
                                applicants.Remove(player);
                                _log.Trace("[Matchmaking] Removing " + character.Name + " from matchmaking.");
                                return;
                            }
                        }
                    }
                }
                // Removes player from an invited game if they decline.
                foreach (var game in _instantGames)
                {
                    if (game.RemovePlayer(character))
                    {
                        _log.Trace("[Matchmaking] " + character.Name + " declined arena invitation.");
                        OnInvitationRemoved(game);
                        return;
                    }
                }
            }
        }
    }

    // Called under the persistence lock after the player has left the roster.
    internal void OnInvitationRemoved(InstantGame game)
    {
        lock (_lock)
        {
            if (game.IsFinished || !_instantGames.Contains(game) || game.TryAbandonBeforeEntry())
                return;
            if (!_queueList.Contains(game))
                _queueList.Add(game);
        }
    }

    private void CheckMatchmakingQueue(uint bfId)
    {
        InstantGame game;
        var games = _queueList.Where(o => o.BattlefieldId == bfId).ToList();
        if (!_matchmakingQueue.TryGetValue(bfId, out var applicants))
        {
            return;
        }

        var world = WorldManager.Instance;
        applicants.RemoveAll(applicant =>
            world.GetCharacterByObjId(applicant.CharObj.ObjId) != applicant.CharObj ||
            !PrisonerAccess.CanEnter(applicant.CharObj, true));

        _log.Trace("[Matchmaking] Running matchmaking for battlefield " + bfId + "... Queue: " + applicants.Count);
        var battlefield = BattlefieldGameData.Instance.GetBattlefield(bfId);
        var playersNeeded = games.Count == 0 ? battlefield.RuleSet.CorpsSize * 2 : 1;
        if (applicants.Count < playersNeeded)
        {
            _log.Trace("[Matchmaking] {0} more players needed for battlefield {1}", playersNeeded - applicants.Count, bfId);
            return;
        }

        // Games without sufficient players are prioritized before new games are made.
        if (games.Count > 0)
        {
            _log.Trace("[Matchmaking] Game found without full players.");
            game = games[0];
        }
        else // Create a new game if there is no current one to matchmake for.
            game = new InstantGame(battlefield);

        // Loop through the matchmaking list to fill current and new games.
        var queueCount = applicants.Count;
        for (var i = 0; i < queueCount; i++)
        {
            if (game.IsFull)
            {
                _log.Trace("[Matchmaking] Game is full.");
                if (_queueList.Remove(game))
                {
                    _log.Trace("[Matchmaking] Removing queued game from queueList.");
                }
                break; // Matchmaking complete if game is full.
            }

            // Obtain character of player matchmaking and remove them from queue to add them into a game.
            var playerCharacter = applicants[0].CharObj;
            applicants.RemoveAt(0);

            // Add player and invite to instant game
            if (world.GetCharacterByObjId(playerCharacter.ObjId) == playerCharacter)
            {
                game.AddPlayer(playerCharacter, game.GetCorps());
                _log.Trace("[Matchmaking] Adding player to game: " + playerCharacter.Name);
            }
        }
        // Add new arena with paired players if there are no queued games to fill up.
        if (!_instantGames.Contains(game) && games.Count == 0)
        {
            _log.Trace("[Matchmaking] Instant game created.");
            _instantGames.Add(game);
        }
        if (game.IsFull)
        {
            _log.Trace("[Matchmaking] Game is full.");
            if (_queueList.Remove(game))
            {
                _log.Trace("[Matchmaking] Removing queued game from queueList.");
            }
        }
        else if (!game.IsFull && bfId != (uint)InstantGameType.Gladiator)
        {
            if (!_queueList.Contains(game))
            {
                // Save the game into queued lists if it is not full yet.
                _log.Trace("[Matchmaking] Adding Drill Camp game to queue list as it is not full.");
                _queueList.Add(game);
            }
        }
    }

    public bool PlayerCanEnter(Character character)
    {
        if (!PrisonerAccess.CanEnter(character, true))
            return false;
        if (character != null)
        {
            // TODO: Not in jury, not in duel conditions
            if (character.IsInBattle)
                return false; // In Combat
            else if (character.Transform.InstanceId != WorldManager.DefaultInstanceId)
                return false; // In an instanced world (Dungeon or Mirage)
            else if (character.IsDead)
                return false; // Is dead
            else if (character.Inventory.Equipment.GetItemBySlot(26) != null)
                return false; // Tradepack equipped
            else if (character.Buffs.CheckBuff(2385))
                return false; // Rebirth trauma is active
            else if (WorldManager.Instance.GetWorld(character.Transform.InstanceId)?.SlaveManager.GetActiveSlaveByOwnerObjId(character.ObjId) != null)
                return false; // Vehicle or boat is summoned
            else
                return true;
        }
        return false; // Character is null
    }

    public void BattlefieldTick(TimeSpan delta)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            lock (_lock)
            {
                foreach (var (bfId, players) in _matchmakingQueue)
                {
                    CheckMatchmakingQueue(bfId);
                }
            }
        }
    }

    internal void StopRefill(InstantGame game) => _queueList.Remove(game);

    public void RemoveGame(InstantGame game)
    {
        StopRefill(game);
        _instantGames.Remove(game);
    }
}
