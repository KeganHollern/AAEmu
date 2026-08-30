using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Indun;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using Microsoft.Extensions.Options;
using NLog;

namespace AAEmu.Game.Core.Managers;

// ReSharper disable once ClassNeverInstantiated.Global
public class IndunManager(
    ITickManager tickManager,
    IWorldManager worldManager,
    IZoneManager zoneManager,
    ITeamManager teamManager,
    TimeProvider timeProvider,
    IOptions<AppConfiguration> appConfiguration) : Singleton<IndunManager>, IIndunManager
{
    // ReSharper disable once InconsistentNaming
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, Dictionary<uint, List<DateTimeOffset>>> CreationHistory { get; } = [];
    // ReSharper disable once ChangeFieldTypeToSystemThreadingLock
    private readonly object _lock = new();
    // ReSharper disable once ChangeFieldTypeToSystemThreadingLock
    private readonly object _dungeonRequestLock = new();

    public void Initialize()
    {
        tickManager.OnTick.Subscribe(IndunInfoTick, TimeSpan.FromSeconds(30), true);
    }

    private void IndunInfoTick(TimeSpan delta)
    {
        PruneCreationHistory();

        var sysInstanceCount = 0;
        var dungeonInstanceCount = 0;
        var worldList = worldManager.GetWorlds().ToList();

        // Count dungeons
        foreach (var worldInstance in worldList)
        {
            if (worldInstance.DungeonInstance != null)
            {
                if (worldInstance.DungeonInstance.IsSystem)
                {
                    sysInstanceCount++;
                }
                else
                {
                    dungeonInstanceCount++;
                }
            }
        }

        if (sysInstanceCount + dungeonInstanceCount <= 0)
            return;
        
        Logger.Info($"Active Instances: {sysInstanceCount} system instance(s), {dungeonInstanceCount} dungeon(s)");

        if (dungeonInstanceCount <= 0)
            return;

        // enumerate dungeon info
        foreach (var worldInstance in worldList)
        {
            if (worldInstance.DungeonInstance != null)
            {
                Logger.Debug($"{worldInstance} - used by {worldInstance.GetCharacterCount()}/{worldInstance.DungeonInstance.PlayersWithAccess.Count} player(s): {worldInstance.ListPlayerNames(10)}");
                if (worldInstance.DungeonInstance.IsExpired)
                {
                    Logger.Warn($"Removing expired solo dungeon {worldInstance}");
                    worldInstance.DungeonInstance.DestroyDungeon();
                }
                // aaemu-cluster#92 (#102): abandoned instances used to survive until the 24h expiry
                // above; reclaim them once they have been empty past the grace period instead.
                else if (worldInstance.DungeonInstance.IsAbandoned)
                {
                    Logger.Warn($"Removing abandoned empty dungeon {worldInstance}");
                    // TryDestroyAbandoned re-checks under the dungeon lock so a player queueing
                    // right now either lands first (no longer abandoned) or is refused and falls
                    // through to a fresh instance. (aaemu-cluster#92, #102)
                    worldInstance.DungeonInstance.TryDestroyAbandoned();
                }
            }
        }

        InfoCreationHistory();
    }

    /// <summary>
    /// Checks if the dungeon for a given zone requires a channel select
    /// </summary>
    /// <param name="zoneId"></param>
    /// <returns></returns>
    public bool InstanceHasChannels(uint zoneId)
    {
        var dungeonZone = IndunGameData.Instance.GetDungeonZone(zoneManager.GetZoneById(zoneId).GroupId);
        return dungeonZone.SelectChannel;
    }

    /// <summary>
    /// Requests an instance for the character's team or for the player.
    /// </summary>
    /// <param name="character"></param>
    /// <param name="zoneId"></param>
    /// <param name="channelId"></param>
    /// <param name="dungeon"></param>
    /// <returns></returns>
    public bool RequestSystemInstance(Character character, uint zoneId, uint channelId, out Dungeon dungeon)
    {
        dungeon = null;
        if (character == null)
        {
            Logger.Info("[IndunManager] Player offline.");
            return false;
        }

        var zone = zoneManager.GetZoneById(zoneId);
        if (zone == null)
        {
            Logger.Warn($"Requesting non existing system instance for zone {zoneId}, character {character.Name}");
            return false;
        }

        dungeon = CreateSystemInstance(character, zone.ZoneKey, channelId);
        if (dungeon == null)
        {
            Logger.Error($"Failed to create system instance for zoneId {zoneId}, channel: {channelId}, character {character.Name}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Requests an instance for the character's team or for the player.
    /// </summary>
    /// <param name="character"></param>
    /// <param name="zoneId"></param>
    /// <param name="channelId"></param>
    /// <returns></returns>
    public bool RequestDungeonInstance(Character character, uint zoneId, uint channelId)
    {
        return RunDungeonRequestSerialized(() => RequestDungeonInstanceCore(character, zoneId, channelId));
    }

    /// <summary>
    /// Serializes dungeon owner lookup and creation so concurrent party requests cannot publish
    /// separate instances from the same stale world snapshot. (aaemu-cluster#102)
    /// </summary>
    internal T RunDungeonRequestSerialized<T>(Func<T> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_dungeonRequestLock)
        {
            return request();
        }
    }

    private bool RequestDungeonInstanceCore(Character character, uint zoneId, uint channelId)
    {
        if (character == null)
        {
            Logger.Info($"Player requested a dungeon, but is now offline.");
            return false;
        }
        var team = teamManager.GetTeamByObjId(character.ObjId);
        var zone = zoneManager.GetZoneById(zoneId);

        // Check valid zone/dungeon
        var worldTemplate = worldManager.GetWorldTemplateByZoneKey(zone.ZoneKey);
        if (worldTemplate == null)
        {
            // Non-existing dungeon zone
            return false;
        }

        var targetZone = zoneManager.GetZoneById(zoneId);
        if (targetZone == null)
        {
            // Key does not match any zone
            return false;
        }
        
        var dungeonZone = IndunGameData.Instance.GetDungeonZone(targetZone.GroupId);
        if (dungeonZone == null)
        {
            // Not a dungeon
            return false;
        }

        var possibleTargetInstances = GetExistingDungeonsByZoneKey(targetZone.ZoneKey);

        // A party can form while its owner is still inside a solo dungeon. Promote that active
        // instance before the owner lookup so a concurrent teammate cannot create a second one.
        // Empty solo instances keep their existing stale-instance cleanup behavior.
        if (team != null && !possibleTargetInstances.Any(instance =>
                !instance.IsDestroyed && instance.World != null && !instance.IsSystem &&
                instance.IsTeamOwned && instance.GetOwnerTeam?.Id == team.Id))
        {
            foreach (var possibleTargetInstance in possibleTargetInstances)
            {
                if (possibleTargetInstance.TryPromoteActiveSoloToTeam(team))
                    break;
            }
        }

        // 1 - Requests already being processed and players already inside do not create a new instance.
        foreach (var possibleTargetInstance in possibleTargetInstances)
        {
            // Skip instances that are torn down (or being torn down by the sweep) — their World
            // reference may already be null. (aaemu-cluster#92, #102)
            if (possibleTargetInstance.IsDestroyed || possibleTargetInstance.World == null)
                continue;

            if (possibleTargetInstance.EnterRequests.Contains(character))
            {
                character.SendErrorMessage(ErrorMessageType.TryLaterInstance); // probably not a good error for this
                return true;
            }

            if (possibleTargetInstance.World.HasCharacter(character.Id))
            {
                possibleTargetInstance.AddPlayer(character);
                return true;
            }
        }

        // Check non-consuming requirements before joining or creating an instance.
        if (!VerifyDungeonEnterRequirements(dungeonZone, character, team))
        {
            return false;
        }

        // aaemu-cluster#92 (#102): the reuse key is the OWNER (team or character), not per-character
        // access. Access-based reuse routed party members back into their old abandoned solo
        // instances instead of into one shared team instance.
        if (team != null)
        {
            // 2a - The character's old solo instances must not be reused while they are in a team.
            // Unbind them and destroy the ones that sit empty so they cannot leak until the 24h expiry.
            foreach (var possibleTargetInstance in possibleTargetInstances)
            {
                if (possibleTargetInstance.IsDestroyed || possibleTargetInstance.IsSystem || possibleTargetInstance.IsTeamOwned)
                    continue;
                if (possibleTargetInstance.GetCharacterOwner?.Id != character.Id)
                    continue;

                possibleTargetInstance.PlayersWithAccess.Remove(character.Id);
                if (possibleTargetInstance.IsEmpty && possibleTargetInstance.EnterRequests.Count == 0)
                {
                    Logger.Info($"Removing stale solo dungeon of {character.Name} ({character.Id}), zone: {dungeonZone}");
                    possibleTargetInstance.DestroyDungeon();
                }
                // Not empty: the empty-instance sweep in IndunInfoTick removes it once it drains
            }

            // 2b - In a team only the instance owned by this team may be reused,
            // this also covers PartyOnly dungeons like Sharpwind Mines
            foreach (var possibleTargetInstance in possibleTargetInstances)
            {
                if (possibleTargetInstance.IsDestroyed || possibleTargetInstance.World == null ||
                    possibleTargetInstance.IsSystem || !IsOwnedByRequester(
                        possibleTargetInstance.IsTeamOwned,
                        possibleTargetInstance.GetCharacterOwner?.Id,
                        possibleTargetInstance.GetOwnerTeam?.Id,
                        character.Id,
                        team.Id))
                    continue;

                // Join your team's dungeon (if enough room)
                if (possibleTargetInstance.IsFull)
                {
                    character.SendErrorMessage(ErrorMessageType.InstanceQuota); // Too many users are currently in the dungeon
                    return false;
                }

                // Fall through to creating a fresh instance ONLY when the sweep destroyed this one
                // between our check and the queue attempt; other refusals (court case, required item)
                // must not orphan a brand-new instance. (aaemu-cluster#92, #102, review)
                if (QueuePlayerWithRequiredItem(possibleTargetInstance, character))
                    return true;
                if (possibleTargetInstance.IsDestroyed)
                    break;
                return false;
            }
        }
        else
        {
            // 3 - Solo players may only reuse the dungeon they own themselves
            foreach (var possibleTargetInstance in possibleTargetInstances)
            {
                if (possibleTargetInstance.IsDestroyed || possibleTargetInstance.World == null ||
                    possibleTargetInstance.IsSystem || !IsOwnedByRequester(
                        possibleTargetInstance.IsTeamOwned,
                        possibleTargetInstance.GetCharacterOwner?.Id,
                        possibleTargetInstance.GetOwnerTeam?.Id,
                        character.Id,
                        null))
                    continue;

                // Re-enter own dungeon if not full yet (MaxPlayers still applies, e.g. Sharpwind Mines = 3)
                if (possibleTargetInstance.IsFull)
                {
                    character.SendErrorMessage(ErrorMessageType.InstanceQuota); // Too many users are currently in the dungeon
                    return false;
                }

                if (QueuePlayerWithRequiredItem(possibleTargetInstance, character))
                    return true;
                if (possibleTargetInstance.IsDestroyed)
                    break;
                return false;
            }
        }

        // 5 - If none of the above applies, actually create a new dungeon
        Logger.Info($"Creating a new dungeon for player {character.Name} ({character.Id}), zone: {dungeonZone}, channel: {channelId}");
        if (!CreateDungeonInstance(dungeonZone, character, channelId, team, out var dungeon))
        {
            Logger.Error($"Failed to create a new dungeon for player {character.Name} ({character.Id}), zone: {dungeonZone}, channel: {channelId}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Matches dungeon reuse by its owner, never by a character's access list. (aaemu-cluster#102)
    /// </summary>
    internal static bool IsOwnedByRequester(
        bool candidateIsTeamOwned,
        uint? candidateCharacterOwnerId,
        uint? candidateTeamOwnerId,
        uint requesterCharacterId,
        uint? requesterTeamId)
    {
        return requesterTeamId.HasValue
            ? candidateIsTeamOwned && candidateTeamOwnerId == requesterTeamId
            : !candidateIsTeamOwned && candidateCharacterOwnerId == requesterCharacterId;
    }

    /// <summary>
    /// Promotes a live solo dungeon through the same critical section as dungeon requests.
    /// </summary>
    internal bool TryPromoteDungeonToTeam(Dungeon dungeon, Team team)
    {
        if (dungeon == null || team == null)
            return false;

        return RunDungeonRequestSerialized(() =>
        {
            var hasTeamDungeon = worldManager.GetWorlds().Any(world =>
                !ReferenceEquals(world.DungeonInstance, dungeon) &&
                world.DungeonInstance is
                {
                    IsDestroyed: false,
                    IsSystem: false,
                    IsTeamOwned: true
                } existingDungeon &&
                existingDungeon.World != null &&
                existingDungeon.GetZoneGroupId == dungeon.GetZoneGroupId &&
                existingDungeon.GetOwnerTeam?.Id == team.Id);

            return !hasTeamDungeon && dungeon.TryPromoteActiveSoloToTeam(team);
        });
    }

    /// <summary>
    /// Creates a list of all currently active dungeons that have a given zone
    /// </summary>
    /// <param name="zoneKey">Required Zone Key for the dungeons</param>
    /// <returns></returns>
    private List<Dungeon> GetExistingDungeonsByZoneKey(uint zoneKey)
    {
        var res = new List<Dungeon>();
        foreach (var worldInstance in worldManager.GetWorlds())
        {
            if (worldInstance.DungeonInstance == null)
                continue;
            if (worldInstance.Template.ZoneKeys.Contains(zoneKey))
                res.Add(worldInstance.DungeonInstance);
        }
        return res;
    }

    /// <summary>
    /// Check if the player has the level, items and other requirements to be allowed to enter the given dungeon zone
    /// </summary>
    /// <param name="dungeonZone"></param>
    /// <param name="character"></param>
    /// <param name="team"></param>
    /// <returns></returns>
    private bool VerifyDungeonEnterRequirements(IndunZone dungeonZone, Character character, Team team)
    {
        if (TrialManager.Instance.IsPlayerInCourt(character.Id))
        {
            character.SendErrorMessage(ErrorMessageType.CannotUsePortalInTrial);
            return false;
        }

        // Check Level requirement
        if (character.Level < dungeonZone.LevelMin)
        {
            Logger.Warn($"Requesting instance level too low ({character.Level} < {dungeonZone.LevelMin}), characterId: {character.Id}, zoneGroupId: {dungeonZone.ZoneGroupId}");
            character.SendErrorMessage(ErrorMessageType.InstanceLevel);
            return false;
        }
        if (character.Level > dungeonZone.LevelMax)
        {
            Logger.Warn($"Requesting instance level too high ({character.Level} > {dungeonZone.LevelMax}), characterId: {character.Id}, zoneGroupId: {dungeonZone.ZoneGroupId}");
            character.SendErrorMessage(ErrorMessageType.InstanceLevel);
            return false;
        }
        
        // Check party status
        if (dungeonZone.PartyOnly && team == null)
        {
            Logger.Warn($"Requesting instance team required, characterId: {character.Id}, zoneGroupId: {dungeonZone.ZoneGroupId}");
            character.SendErrorMessage(ErrorMessageType.NeedParty);
            return false;
        }
        
        return true;
    }

    private static bool ConsumeDungeonEntryItem(IndunZone dungeonZone, Character character)
    {
        if (dungeonZone.ItemId <= 0 || PortalManager.CheckItemAndRemove(character, dungeonZone.ItemId, 1))
        {
            return true;
        }

        Logger.Info($"[IndunManager] Player does not have the required item to enter a dungeon, characterId: {character.Id}, zoneGroupId: {dungeonZone.ZoneGroupId}, item: {dungeonZone.ItemId}");
        character.SendErrorMessage(ErrorMessageType.EnterInstReqItem, dungeonZone.ItemId);
        return false;
    }

    private static bool QueuePlayerWithRequiredItem(Dungeon dungeon, Character character)
    {
        return dungeon.QueuePlayer(character, () => ConsumeDungeonEntryItem(dungeon._indunZone, character));
    }

    /// <summary>
    /// Creates a new player created dungeon instance
    /// </summary>
    /// <param name="dungeonZone"></param>
    /// <param name="character"></param>
    /// <param name="channelId"></param>
    /// <param name="dungeon"></param>
    /// <returns></returns>
    private bool CreateDungeonInstance(IndunZone dungeonZone, Character character, uint channelId, Team team, out Dungeon dungeon)
    {
        dungeon = null;

        // Check if we have capacity
        if (worldManager.GetWorlds().Length > appConfiguration.Value.World.MaxInstances)
        {
            Logger.Warn($"Requesting a new instance would exceeds the allowed ammount, characterId: {character.Id}, zoneGroupId: {dungeonZone.ZoneGroupId}");
            character.SendErrorMessage(ErrorMessageType.NoServerInstanceResource);
            return false;
        }

        Logger.Info($"Requesting instance, characterId: {character.Id}, zoneGroupId: {dungeonZone.ZoneGroupId}");

        if (!TryReserveDungeonCreation(character.Id, dungeonZone.ZoneGroupId, out var reservationTime))
        {
            var config = appConfiguration.Value.Dungeons;
            Logger.Warn($"Requesting instance too many recent creations ({config.CreationLimit} in {config.CreationWindowMinutes} minutes), characterId: {character.Id}, zoneGroupId: {dungeonZone.ZoneGroupId}");
            character.SendErrorMessage(ErrorMessageType.InstanceVisitLimit);
            return false;
        }

        if (!ConsumeDungeonEntryItem(dungeonZone, character))
        {
            ReleaseDungeonCreationReservation(character.Id, dungeonZone.ZoneGroupId, reservationTime);
            return false;
        }

        try
        {
            dungeon = new Dungeon(dungeonZone, character, channelId, team);
            if (dungeon.QueuePlayer(character))
            {
                return true;
            }

            dungeon.DestroyDungeon();
            dungeon = null;
            ReleaseDungeonCreationReservation(character.Id, dungeonZone.ZoneGroupId, reservationTime);
            return false;
        }
        catch
        {
            ReleaseDungeonCreationReservation(character.Id, dungeonZone.ZoneGroupId, reservationTime);
            throw;
        }
    }

    /// <summary>
    /// Creates and returns a system instance with a given channel
    /// </summary>
    /// <param name="character"></param>
    /// <param name="zoneKey"></param>
    /// <param name="channelId"></param>
    /// <param name="overrideInstanceId"></param>
    /// <param name="fixedInstanceId"></param>
    /// <returns></returns>
    public Dungeon CreateSystemInstance(Character character, uint zoneKey, uint channelId, bool overrideInstanceId = false, uint fixedInstanceId = 0)
    {
        Logger.Info($"Requesting system instance, zoneKey: {zoneKey}, character: {character?.Name ?? "[SYSTEM]"}, channel: {channelId}, override InstanceId: {(overrideInstanceId ? fixedInstanceId.ToString() : "NO")}");

        var team = character != null ? teamManager.GetTeamByObjId(character.ObjId) : null;
        var zone = zoneManager.GetZoneByKey(zoneKey);
        var dungeonZone = zone == null ? null : IndunGameData.Instance.GetDungeonZone(zone.GroupId);
        if (dungeonZone == null)
        {
            Logger.Error($"Requesting invalid system instance: , zoneKey: {zoneKey}, character: {character?.Name ?? "[SYSTEM]"}, channel: {channelId}, override InstanceId: {(overrideInstanceId ? fixedInstanceId.ToString() : "NO")}");
            return null;
        }
        
        // Check for duplicate system instances
        foreach (var worldInstance in worldManager.GetWorlds())
        {
            if (worldInstance.ChannelId == channelId &&
                worldInstance.DungeonInstance?.GetZoneGroupId == dungeonZone.ZoneGroupId)
            {
                if (character == null || worldInstance.DungeonInstance.EnterRequests.Contains(character) || worldInstance.HasCharacter(character.Id))
                {
                    return worldInstance.DungeonInstance;
                }

                if (!VerifyDungeonEnterRequirements(dungeonZone, character, team) ||
                    !QueuePlayerWithRequiredItem(worldInstance.DungeonInstance, character))
                {
                    return null;
                }

                return worldInstance.DungeonInstance;
            }
        }

        // Check if zones match
        if (dungeonZone.ZoneGroupId != zone.GroupId)
        {
            Logger.Info("[IndunManager] system dungeon request on different area.");
            character?.SendErrorMessage(ErrorMessageType.ProhibitedInInstance);
            return null;
        }

        if (character != null &&
            (!VerifyDungeonEnterRequirements(dungeonZone, character, team) || !ConsumeDungeonEntryItem(dungeonZone, character)))
        {
            return null;
        }

        // Create new system instance
        var dungeon = new Dungeon(dungeonZone, character, channelId, team, overrideInstanceId, fixedInstanceId)
        {
            IsSystem = true
        };

        if (character != null && !dungeon.QueuePlayer(character))
        {
            dungeon.DestroyDungeon();
            return null;
        }

        return dungeon;
    }

    /// <summary>
    /// Player requesting to remove dungeon with a given zone
    /// </summary>
    /// <param name="character"></param>
    /// <param name="zone"></param>
    /// <returns></returns>
    public bool RequestDeletion(Character character, Zone zone)
    {
        if (character == null)
        {
            return false;
        }
        if (zone == null)
        {
            character.SendErrorMessage(ErrorMessageType.AlreadyUnboundInstance);
            return false;
        }

        var removedCount = 0;
        var dungeons = GetExistingDungeonsByZoneKey(zone.ZoneKey);
        foreach (var dungeon in dungeons)
        {
            if (dungeon.IsSystem)
                continue;

            if (!dungeon.PlayersWithAccess.Contains(character.Id))
                continue;

            // Remove player's own access flag
            dungeon.PlayersWithAccess.Remove(character.Id);
            removedCount++;

            // If nobody has access anymore, remove the dungeon
            if (dungeon.PlayersWithAccess.Count == 0)
            {
                dungeon.DestroyDungeon();
            }
        }

        if (removedCount <= 0)
        {
            character.SendErrorMessage(ErrorMessageType.AlreadyUnboundInstance);
        }
        return true;
    }

    /// <summary>
    /// Player requesting to leave the dungeon/instance 
    /// </summary>
    /// <param name="character"></param>
    /// <returns></returns>
    public bool RequestLeaveInstance(Character character)
    {
        if (character == null)
            return false;
        
        // Remove from all possible different types of dungeons
        // System dungeons (mirage/library)
        foreach (var worldInstance in worldManager.GetWorlds().Where(w => w.HasCharacter(character.Id)))
        {
            
            character.Events.OnDungeonLeave(worldInstance, new OnDungeonLeaveArgs { Player = character });
            // dungeon.LeaveSysInstance(character); // Already called in the OnDungeonLeave event
            return true;
        }

        // No instance found that needs exiting
        return false;
    }

    public void DoIndunActions(uint startActionId, WorldInstance worldInstance)
    {
        while (true)
        {
            var action = IndunGameData.Instance.GetIndunActionById(startActionId);
            action.Execute(worldInstance);
            Logger.Warn($"DoIndunActions: world={worldInstance.Id}, action.Id={action.Id}, action.NextActionId={action.NextActionId}");
            if (action.NextActionId > 0)
            {
                startActionId = action.NextActionId;
                continue;
            }

            break;
        }
    }

    internal bool TryReserveDungeonCreation(uint characterId, uint zoneGroupId, out DateTimeOffset reservationTime)
    {
        reservationTime = default;
        var config = appConfiguration.Value.Dungeons;
        if (config.CreationLimit == 0 || config.CreationWindowMinutes == 0)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            PruneCreationHistoryUnsafe(now, config.CreationWindowMinutes);

            if (!CreationHistory.TryGetValue(characterId, out var zoneAndCreations))
            {
                zoneAndCreations = [];
                CreationHistory.Add(characterId, zoneAndCreations);
            }

            if (!zoneAndCreations.TryGetValue(zoneGroupId, out var creationTimes))
            {
                creationTimes = [];
                zoneAndCreations.Add(zoneGroupId, creationTimes);
            }

            if (creationTimes.Count >= config.CreationLimit)
            {
                return false;
            }

            creationTimes.Add(now);
            reservationTime = now;
            Logger.Debug($"Reserved dungeon creation for player {characterId} in zone group {zoneGroupId}, count is now {creationTimes.Count}/{config.CreationLimit}");
            return true;
        }
    }

    internal void ReleaseDungeonCreationReservation(uint characterId, uint zoneGroupId, DateTimeOffset reservationTime)
    {
        lock (_lock)
        {
            if (!CreationHistory.TryGetValue(characterId, out var zoneAndCreations) ||
                !zoneAndCreations.TryGetValue(zoneGroupId, out var creationTimes))
            {
                return;
            }

            creationTimes.Remove(reservationTime);
            if (creationTimes.Count == 0)
            {
                zoneAndCreations.Remove(zoneGroupId);
            }

            if (zoneAndCreations.Count == 0)
            {
                CreationHistory.Remove(characterId);
            }
        }
    }

    internal int GetRecentDungeonCreationCount(uint characterId, uint zoneGroupId)
    {
        var config = appConfiguration.Value.Dungeons;
        if (config.CreationLimit == 0 || config.CreationWindowMinutes == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            PruneCreationHistoryUnsafe(now, config.CreationWindowMinutes);
            return CreationHistory.TryGetValue(characterId, out var zoneAndCreations) &&
                zoneAndCreations.TryGetValue(zoneGroupId, out var creationTimes)
                    ? creationTimes.Count
                    : 0;
        }
    }

    private void PruneCreationHistory()
    {
        var config = appConfiguration.Value.Dungeons;
        lock (_lock)
        {
            if (config.CreationLimit == 0 || config.CreationWindowMinutes == 0)
            {
                CreationHistory.Clear();
                return;
            }

            PruneCreationHistoryUnsafe(timeProvider.GetUtcNow(), config.CreationWindowMinutes);
        }
    }

    private void PruneCreationHistoryUnsafe(DateTimeOffset now, uint creationWindowMinutes)
    {
        var thresholdTime = now.AddMinutes(-creationWindowMinutes);
        foreach (var (characterId, zoneAndCreations) in CreationHistory.ToList())
        {
            foreach (var (zoneGroupId, creationTimes) in zoneAndCreations.ToList())
            {
                creationTimes.RemoveAll(creationTime => creationTime <= thresholdTime);
                if (creationTimes.Count == 0)
                {
                    zoneAndCreations.Remove(zoneGroupId);
                }
            }

            if (zoneAndCreations.Count == 0)
            {
                CreationHistory.Remove(characterId);
            }
        }
    }

    private void InfoCreationHistory()
    {
        lock (_lock)
        {
            foreach (var (characterId, zoneAndCreations) in CreationHistory)
            {
                foreach (var (zoneGroupId, creationTimes) in zoneAndCreations)
                {
                    Logger.Debug($"For player={characterId} ({worldManager.GetCharacterById(characterId)?.Name}): {creationTimes.Count} recent dungeon creations in zone group {zoneGroupId} ({zoneManager.GetZoneGroupById(zoneGroupId)?.Name})");
                }
            }
        }
    }
}
