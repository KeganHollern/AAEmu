using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Skills;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class InstantGameManager : Singleton<InstantGameManager>, IInstantGameManager
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<uint, List<MatchmakingApplicant>> _matchmakingQueue = [];

    private readonly List<InstantGame> _instantGames = [];

    private readonly List<InstantGame> _queueList = [];

    private readonly Lock _lock = new();
    private readonly Dictionary<GladiatorRankResult, TaskCompletionSource> _pendingRankResults = [];

    // Called under the Game persistence lock. Every published end has a committed result.
    internal Task RecordRankResult(GladiatorRankResult result)
    {
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRankResults.Add(result, saved);
        _ = Task.Run(() =>
        {
            try
            {
                using var connection = MySQL.CreateConnection();
                result.Write(connection);
                lock (SaveManager.PersistenceSyncRoot)
                    MarkRankSaved(result);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Could not save Gladiator result {0}. The next Game checkpoint retries it.", result.MatchId);
            }
        });
        return saved.Task;
    }

    // The normal save and the host shutdown checkpoint each try pending outcomes once.
    internal void SavePendingRankResults(PersistenceSaveContext context)
    {
        foreach (var result in _pendingRankResults.Keys)
        {
            result.Write(context.Connection, context.Transaction);
            context.AfterCommit(() => MarkRankSaved(result));
        }
    }

    private void MarkRankSaved(GladiatorRankResult result)
    {
        if (_pendingRankResults.Remove(result, out var saved))
            saved.TrySetResult();
    }

    public void Initialize()
    {
        // 15 seconds between each matchmaking query
        TickManager.Instance.OnTick.Subscribe(BattlefieldTick, TimeSpan.FromSeconds(15));
    }

    public void ApplyToBattlefield(uint battlefieldId, InstantCorps corps, Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!PlayerCanEnter(character))
                return;

            lock (_lock)
            {
                if (WorldManager.Instance.GetCharacterByObjId(character.ObjId) != character ||
                    character.CurrentInstantGame != null || _instantGames.Any(game => game.HasPlayer(character.Id)) ||
                    _matchmakingQueue.Values.Any(queue => queue.Any(applicant => applicant.CharObj.Id == character.Id)))
                    return;

                var battlefield = BattlefieldGameData.Instance.GetBattlefield(battlefieldId);
                if (battlefield?.HasMatchData != true)
                {
                    _log.Warn("[Matchmaking] Battlefield {0} has no complete match configuration.", battlefieldId);
                    return;
                }
                if (!battlefield.CheckEntryLevel(character))
                    return;

                if (!_matchmakingQueue.TryGetValue(battlefieldId, out var applicants))
                {
                    applicants = [];
                    _matchmakingQueue.Add(battlefieldId, applicants);
                }

                applicants.Add(new MatchmakingApplicant(character));
            }

            _log.Trace("[Matchmaking] Added player " + character.Name + " to matchmaking queue for battlefield " +
                       battlefieldId);

            character.SendPacket(new SCAppliedToInstantGamePacket(battlefieldId, corps));
        }
    }

    // The caller holds the persistence lock when it removes this session from the world.
    internal void OnCharacterDisconnected(Character character)
    {
        lock (_lock)
        {
            RemoveApplications(character);
            character.CurrentInstantGame?.DisconnectPlayer(character);
        }
    }

    private bool RemoveApplications(Character character)
    {
        var removed = 0;
        foreach (var applicants in _matchmakingQueue.Values)
            removed += applicants.RemoveAll(applicant => applicant.CharObj == character);
        return removed > 0;
    }

    internal void CancelAdmissions(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            lock (_lock)
            {
                RemoveApplications(character);
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
                if (RemoveApplications(character))
                {
                    _log.Trace("[Matchmaking] Removing " + character.Name + " from matchmaking.");
                    return;
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

        var battlefield = BattlefieldGameData.Instance.GetBattlefield(bfId);
        var world = WorldManager.Instance;
        applicants.RemoveAll(applicant =>
            world.GetCharacterByObjId(applicant.CharObj.ObjId) != applicant.CharObj ||
            !PlayerCanEnter(applicant.CharObj) || !battlefield.CheckEntryLevel(applicant.CharObj));

        _log.Trace("[Matchmaking] Running matchmaking for battlefield " + bfId + "... Queue: " + applicants.Count);
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

    internal static bool PlayerCanEnter(Character character)
    {
        if (!PrisonerAccess.CanEnter(character, true))
            return false;
        if (character.Buffs.CheckBuff((uint)BuffConstants.ArenaDeserter))
            character.SendErrorMessage(ErrorMessageType.RunawayCantApplyBattleField);
        else if (character.Buffs.CheckBuffTag((uint)BuffConstants.TagCannotEnterArena) ||
                 character.Buffs.CheckBuff((uint)BuffConstants.InBeautySalon))
            character.SendErrorMessage(ErrorMessageType.InstantGameCannotJoinByBuff);
        else if (character.ParentWorld?.DungeonInstance != null)
            character.SendErrorMessage(ErrorMessageType.InstantGameCannotJoinByNoSeamless);
        else
            return true;
        return false;
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
