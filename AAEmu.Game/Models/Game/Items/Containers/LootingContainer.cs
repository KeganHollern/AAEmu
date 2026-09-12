using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units;
using NLog;

namespace AAEmu.Game.Models.Game.Items.Containers;

/// <summary>
/// Unlike other item containers this one is not an actual ItemContainer
/// </summary>
public class LootingContainer
{
    private readonly Random _random;
    private readonly Action<AAEmu.Game.Models.Tasks.Task, TimeSpan> _schedule;
    private readonly Func<uint, Character> _findCharacter;

    public LootingContainer(IBaseUnit owner) : this(owner, Random.Shared,
        (task, delay) => TaskManager.Instance.Schedule(task, delay, count: 1),
        id => WorldManager.Instance.GetCharacterById(id))
    {
    }

    internal LootingContainer(IBaseUnit owner, Random random, Action<AAEmu.Game.Models.Tasks.Task, TimeSpan> schedule,
        Func<uint, Character> findCharacter)
    {
        LootOwner = owner;
        _random = random;
        _schedule = schedule;
        _findCharacter = findCharacter;
    }

    // ReSharper disable once FieldCanBeMadeReadOnly.Local
    // ReSharper disable once InconsistentNaming
    private static Logger Logger = LogManager.GetCurrentClassLogger();

    // TODO: Make loot range and timing settings configurable
    /// <summary>
    /// Maximum range from owner to be permitted to loot (don't allow looting by far away people)
    /// </summary>
    public const float MaxLootingRange = 200f;

    // Native loot-bag distance checks use sqrt(9) for NPCs and 9 for doodads.
    internal const float NpcPickupRange = 3f;
    internal const float DoodadPickupRange = 9f;

    // r208022 X2UI loot_dice.alb sends Pass after 60000 milliseconds.
    internal static readonly TimeSpan RollTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Time before loot goes to public in seconds
    /// </summary>
    private const float MakeLootPublicTime = 180f;

    /// <summary>
    /// When loot has been generated, extend the despawn timer by this amount (in seconds)
    /// </summary>
    public const float LootDespawnExtensionTime = 300f;

    /// <summary>
    /// Minimum time that a corpse should remain after it has been looted for all items (if it had items)
    /// </summary>
    private const float PostLootMinimumDespawnTime = 2f;

    /// <summary>
    /// Unit this looting container is attached to
    /// </summary>
    private IBaseUnit LootOwner { get; }
    private LootOwnerType LootOwnerType { get; set; } = LootOwnerType.None;

    /// <summary>
    /// Unit that dealt the killing blow
    /// </summary>
    private IBaseUnit Killer { get; set; }

    private Team.Team KillerTeam { get; set; }
    private LootingRule TeamLootingRule { get; set; }

    /// <summary>
    /// Time this loot was generated
    /// </summary>
    private DateTime CreationTime { get; set; } = DateTime.MinValue;

    /// <summary>
    /// List of item entries (itemIndex, LootItemEntry)
    /// </summary>
    public Dictionary<ushort, LootingContainerItemEntry> Items { get; } = [];
    private object ItemsLock { get; } = new();
    private bool AlreadyGenerated { get; set; }
    private HashSet<Character> EligiblePlayers { get; } = [];
    private HashSet<Character> OpenedBy { get; } = [];

    /// <summary>
    /// Generate appropriate loot
    /// </summary>
    /// <param name="killer"></param>
    public void GenerateLoot(IBaseUnit killer)
    {
        lock (ItemsLock)
            GenerateLootLocked(killer);
    }

    private void GenerateLootLocked(IBaseUnit killer)
    {
        // Do not allow multiple generations of loot 
        if (AlreadyGenerated)
            return;
        AlreadyGenerated = true;

        // Initialize some things
        LootOwnerType = LootOwner switch
        {
            Npc => LootOwnerType.Npc,
            Doodad => LootOwnerType.Doodad,
            _ => LootOwnerType.None
        };
        Killer = killer;
        CreationTime = DateTime.UtcNow;
        lock (ItemsLock)
        {
            Items.Clear();
        }

        // NPC Loot handling
        if (LootOwnerType == LootOwnerType.Npc && LootOwner is Npc npc)
        {
            // Get drop list for this NPC
            var lootPackDroppingNpcs = ItemManager.Instance.GetLootPackIdByNpcId(npc.TemplateId);
            if (lootPackDroppingNpcs.Count <= 0)
            {
                return;
            }

            // Calculate loot rates
            var lootDropRate = 1f;
            var lootGoldRate = 1f;

            // Check all people with a claim on the NPC
            EligiblePlayers.Clear();
            KillerTeam = TeamManager.Instance.GetActiveTeam(npc.CharacterTagging.TagTeam);
            Character[] teamMembers = [];
            if (KillerTeam != null)
            {
                lock (KillerTeam.SyncLock)
                {
                    TeamLootingRule = KillerTeam.LootingRule.Clone();
                    teamMembers = KillerTeam.Members.Where(member => member?.Character != null)
                        .Select(member => member.Character).ToArray();
                }
            }
            else
            {
                TeamLootingRule = new LootingRule { LootMethod = LootingRuleMethod.FreeForAll };
            }

            if (npc.CharacterTagging.TagTeam != 0)
            {
                // A team has tagging rights
                if (KillerTeam != null)
                {
                    foreach (var member in teamMembers)
                    {
                        if (IsWithinLootRange(member))
                            EligiblePlayers.Add(member);
                    }
                }
                else if (npc.CharacterTagging.Tagger != null)
                {
                    // If a team tag, but no valid team found then use the tagger
                    EligiblePlayers.Add(npc.CharacterTagging.Tagger);
                }
            }
            else if (npc.CharacterTagging.Tagger != null)
            {
                // Set to FreeForAll when only the tagger has rights
                TeamLootingRule = new LootingRule
                {
                    LootMethod = LootingRuleMethod.FreeForAll,
                    MinimumGrade = 0,
                    LootMaster = (Killer as Character)?.Id ?? 0
                };
                // A player has tag rights
                EligiblePlayers.Add(npc.CharacterTagging.Tagger);
            }

            // Calculate required drop-rate multipliers
            if (EligiblePlayers.Count > 0)
            {
                var maxDropRateMul = -100f;
                var maxLootGoldMul = -100f;

                foreach (var pl in EligiblePlayers)
                {
                    var aggroDropMul = (100f + pl.DropRateMul) / 100f;
                    var aggroGoldMul = (100f + pl.LootGoldMul) / 100f;
                    if (aggroDropMul > maxDropRateMul)
                        maxDropRateMul = aggroDropMul;
                    if (aggroGoldMul > maxLootGoldMul)
                        maxLootGoldMul = aggroGoldMul;

                }

                lootDropRate = maxDropRateMul;
                lootGoldRate = maxLootGoldMul;
            }
            else if (npc.CharacterTagging.TagTeam == 0 && npc.CharacterTagging.Tagger == null && killer is Character player)
            {
                // Only an untagged kill can fall back to the killer. An absent tagging team keeps its private claim.
                lootDropRate *= (100f + player.DropRateMul) / 100f;
                lootGoldRate *= (100f + player.LootGoldMul) / 100f;
                Logger.Info($"Unit killed without aggro: {npc.ObjId} ({npc.TemplateId}) by {player.Name}");
                EligiblePlayers.Add(player);
            }

            // Base ID used for identifying the loot
            // var baseId = ((ulong)LootOwner.ObjId << 32) + ((ulong)LootOwnerType << 16) + 1;

            // Generate the actual loot
            List<(uint itemId, int count, byte grade, uint lootGroupOrigin)> lootPackResults = [];
            foreach (var lootPackDropping in lootPackDroppingNpcs)
            {
                var lootPack = LootGameData.Instance.GetPack(lootPackDropping.LootPackId);
                if (lootPack == null)
                    continue;
                lootPackResults.AddRange(lootPack.GeneratePackNewV2(lootDropRate, lootGoldRate, killer as Character, ActabilityType.None));
                // var items = lootPack.GenerateNpcPackItems(ref baseId, killer, lootDropRate, lootGoldRate);
                // RegisterItems(items);
            }

            // New Loot, i guess both loops can be optimzed....
            var groups = lootPackResults.GroupBy(x => x.lootGroupOrigin).Select(x => x.Key).ToList();
            foreach (var group in groups)
            {
                var selectByGroup = lootPackResults.Where(x => x.lootGroupOrigin == group).ToList();
                if (selectByGroup.Count > 0)
                {
                    var resultsToAdd = new List<Item>();
                    foreach (var singleItemInGroup in selectByGroup)
                    {
                        var item = ItemManager.Instance.Create(singleItemInGroup.itemId, singleItemInGroup.count, singleItemInGroup.grade, false);
                        resultsToAdd.Add(item);
                    }
                    RegisterItems(resultsToAdd);
                }
            }

            lock (ItemsLock)
            {
                if (Items.Count <= 0)
                    return;
            }

            UpdateLootState();
        }
        else
        if (LootOwnerType == LootOwnerType.Doodad && LootOwner is Doodad doodad)
        {
            // TODO: LootOwnerType.Doodad
            Logger.Warn($"Not yet implemented for doodads, LootOwner: {LootOwnerType}:{doodad.ObjId}");
        }
        else
        {
            // TODO: Either loot generated for a not supported type or it no longer exists 
            Logger.Warn($"Unsupported LootOwner: {LootOwnerType}:{LootOwner.ObjId}");
        }
    }

    /// <summary>
    /// Add generated loot to the loot container
    /// </summary>
    /// <param name="items">Loot</param>
    private void RegisterItems(List<Item> items)
    {
        lock (ItemsLock)
        {
            foreach (var item in items)
            {
                var newItem = new LootingContainerItemEntry
                {
                    Owner = this,
                    Item = item,
                    ItemIndex = (ushort)(Items.Count + 1),
                    HighestRoller = 0
                };
                // Update ItemId to what is expected to be used
                // Note that the actual Item.Id needs to be updated upon actual looting
                newItem.Item.Id = ((ulong)LootOwner.ObjId << 32) + ((ulong)LootOwnerType << 16) + newItem.ItemIndex;

                // Add the actual entry
                Items.Add(newItem.ItemIndex, newItem);
            }
        }
    }

    /// <summary>
    /// Broadcasts packet to all players in the list of targets
    /// </summary>
    /// <param name="players"></param>
    /// <param name="packet"></param>
    private void SendPacketToPlayers(HashSet<Character> players, GamePacket packet)
    {
        Character[] targets;
        lock (ItemsLock)
            targets = players.ToArray();

        foreach (var target in targets)
            target.SendPacket(packet);
    }

    /// <summary>
    /// Sends the SCLootableStatePacket to all involved players and updates despawn times if needed
    /// </summary>
    private void UpdateLootState()
    {
        bool hasItems;
        lock (ItemsLock)
        {
            hasItems = Items.Count > 0;
        }

        SendPacketToPlayers(EligiblePlayers, new SCLootableStatePacket(LootOwnerType, LootOwner.ObjId, hasItems));
        // If no items left, then reduce the despawn timer if needed
        if (!hasItems)
        {
            var minimumDespawnTime = CreationTime;
            switch (LootOwnerType)
            {
                case LootOwnerType.Npc:
                    if (LootOwner is Npc npc)
                    {
                        if (npc.Spawner != null)
                            minimumDespawnTime = CreationTime.AddSeconds(npc.Spawner.DespawnTime);
                        if (minimumDespawnTime < DateTime.UtcNow)
                            minimumDespawnTime = DateTime.UtcNow.AddSeconds(PostLootMinimumDespawnTime);
                        npc.Despawn = minimumDespawnTime;
                    }
                    break;
                default:
                    Logger.Warn($"UpdateLootState, Unsupported LootOwnerType: {LootOwnerType} after looting");
                    break;
            }
        }
    }

    /// <summary>
    /// Player opens the loot bag
    /// </summary>
    /// <param name="player">Player opening the loot container</param>
    /// <param name="object2">unused</param>
    /// <param name="lootAll">True when the player opened the loot using (G) to loot all</param>
    public void OpenBag(Character player, BaseUnit object2, bool lootAll)
    {
        KeyValuePair<ushort, LootingContainerItemEntry>[] lootItems = [];
        lock (ItemsLock)
        {
            if (!CanAccessLoot(player))
                return;

            OpenedBy.Add(player);

            if (lootAll)
                lootItems = Items.ToArray();
        }

        // If LootAll is set, try to loot all items immediately
        if (lootAll)
        {
            // Try to loot all items
            foreach (var (itemIndex, itemEntry) in lootItems)
            {
                TryTakeLoot(player, itemIndex, itemEntry, true);
            }
        }

        // Send packet update of remaining items, or loot state if all has been looted already
        // if (Items.Count <= 0)
        // {
        //     UpdateLootState();
        // }
        // else
        var remainingItems = new List<Item>();
        lock (ItemsLock)
        {
            if (Items.Count > 0)
            {
                foreach (var (_, itemEntry) in Items)
                {
                    remainingItems.Add(itemEntry.Item);
                }
            }
        }

        if (remainingItems.Count > 0)
        {
            SendPacketToPlayers(OpenedBy, new SCLootBagDataPacket(remainingItems, lootAll));
        }
    }

    /// <summary>
    /// Tries to add a LootingContainerItemEntry's item to the player's Bag, does not actually remove the itemEntry
    /// </summary>
    /// <param name="player"></param>
    /// <param name="itemIndex"></param>
    /// <param name="itemEntry"></param>
    /// <param name="didLootAll"></param>
    /// <returns>Returns true if the item was granted to the player</returns>
    public bool TryTakeLoot(Character player, ushort itemIndex, LootingContainerItemEntry itemEntry, bool didLootAll)
    {
        lock (ItemsLock)
        {
            return TryTakeLootLocked(player, itemIndex, itemEntry, didLootAll);
        }
    }

    private bool TryTakeLootLocked(Character player, ushort itemIndex, LootingContainerItemEntry itemEntry, bool didLootAll)
    {
        if (!CanAccessLoot(player) || !Items.TryGetValue(itemIndex, out var currentItemEntry) ||
            (itemEntry != null && itemEntry != currentItemEntry))
            return false;

        itemEntry = currentItemEntry;
        if (itemEntry.HighestRoller > 0 && itemEntry.HighestRoller != player.Id)
        {
            player.SendErrorMessage(ErrorMessageType.NoPermissionToLoot, itemEntry.HighestRoller);
            player.SendPacket(new SCLootItemFailedPacket(ErrorMessageType.NoPermissionToLoot, LootOwnerType, LootOwner.ObjId, itemEntry.ItemIndex, itemEntry.Item.TemplateId));
            return false;
        }

        if (!CanReceiveQuestItem(player, itemEntry))
            return false;

        if (itemEntry.HighestRoller == player.Id)
            return TryDistributeLootToPlayer(player, itemEntry, didLootAll);

        if (itemEntry.RollInProgress)
            return false;

        if (itemEntry.RollCompleted)
            return TryDistributeLootToPlayer(player, itemEntry, didLootAll);

        var rollMandatory = !itemEntry.RollCompleted && TeamLootingRule.LootMethod != LootingRuleMethod.Public &&
            ((TeamLootingRule.MinimumGrade > 0 && itemEntry.Item.Grade >= TeamLootingRule.MinimumGrade) ||
             (TeamLootingRule.RollForBindOnPickup && itemEntry.Item.Template.BindType.HasFlag(ItemBindType.BindOnPickup)));

        // Dice take priority over rotation and master distribution for items that meet the rule.
        if (rollMandatory)
        {
            foreach (var eligiblePlayer in GetCurrentEligiblePlayers().Where(candidate =>
                         IsWithinLootRange(candidate) && HasRequiredQuest(candidate, itemEntry)))
                itemEntry.PlayerRolls.Add(eligiblePlayer, 0);

            if (itemEntry.PlayerRolls.Count > 1)
            {
                itemEntry.RollInProgress = true;
                _schedule(new LootRollTimeoutTask(this, itemEntry), RollTimeout);
                foreach (var character in itemEntry.PlayerRolls.Keys)
                    character.SendPacket(new SCLootDicePacket(itemEntry.Item));
                return false;
            }

            itemEntry.PlayerRolls.Clear();
        }

        var lootTarget = player;
        switch (TeamLootingRule.LootMethod)
        {
            case LootingRuleMethod.FreeForAll:
            case LootingRuleMethod.Public:
                break;
            case LootingRuleMethod.RotateWinner:
                if (EligiblePlayers.Count > 1 && KillerTeam != null)
                {
                    var candidates = GetCurrentEligiblePlayers().Where(candidate =>
                        IsWithinLootRange(candidate) && HasRequiredQuest(candidate, itemEntry)).ToHashSet();
                    lock (KillerTeam.SyncLock)
                        lootTarget = KillerTeam.GetNextLootWinner(candidates, LootOwner);
                    if (lootTarget == null)
                        return false;
                    itemEntry.HighestRoller = lootTarget.Id;
                }
                break;
            case LootingRuleMethod.LootMaster:
                // The rule is a snapshot. A master who left the team cannot receive its later loot.
                if (KillerTeam == null)
                    return false;
                lock (KillerTeam.SyncLock)
                    lootTarget = KillerTeam.Members.FirstOrDefault(member =>
                        member?.Character?.Id == TeamLootingRule.LootMaster)?.Character;
                if (lootTarget == null || !EligiblePlayers.Any(eligible => eligible.Id == lootTarget.Id))
                    return false;
                break;
            default:
                return false;
        }

        var result = TryDistributeLootToPlayer(lootTarget, itemEntry, didLootAll);
        return lootTarget == player && result;
    }

    private IEnumerable<Character> GetCurrentEligiblePlayers()
    {
        // A reconnect keeps corpse eligibility, but an active dice pool keeps the session that received its prompt.
        return EligiblePlayers.Select(player => HasCurrentSession(player) ? player : _findCharacter(player.Id))
            .Where(player => player != null).DistinctBy(player => player.Id);
    }

    private static bool HasCurrentSession(Character player)
    {
        var connection = player?.Connection;
        return player is { IsOnline: true } && connection is { IsClosed: false } &&
               ReferenceEquals(connection.ActiveChar, player);
    }

    private bool IsWithinLootRange(Character player)
    {
        return HasCurrentSession(player) &&
               LootOwner is BaseUnit owner && player.ParentWorld != null &&
               ReferenceEquals(player.ParentWorld, owner.ParentWorld) &&
               Vector3.Distance(player.Transform.World.Position, owner.Transform.World.Position) <= MaxLootingRange;
    }

    private bool CanAccessLoot(Character player)
    {
        if (!CanReceiveLoot(player) || LootOwner is not BaseUnit owner)
            return false;

        // The server uses actor radii for the native collision-shape distance check.
        var distance = Math.Max(0, Vector3.Distance(player.Transform.World.Position, owner.Transform.World.Position) -
                                  player.ModelSize - owner.ModelSize);
        var range = owner is Npc ? NpcPickupRange : DoodadPickupRange;
        return float.IsFinite(distance) && distance < range;
    }

    private bool CanReceiveLoot(Character player)
    {
        return TeamLootingRule != null && IsWithinLootRange(player) &&
               (TeamLootingRule.LootMethod == LootingRuleMethod.Public || EligiblePlayers.Any(eligible => eligible.Id == player.Id));
    }

    private static bool HasRequiredQuest(Character player, LootingContainerItemEntry itemEntry)
    {
        return itemEntry.Item.Template.LootQuestId == 0 ||
               (player.Quests?.HasQuest(itemEntry.Item.Template.LootQuestId) ?? false);
    }

    private bool CanReceiveQuestItem(Character player, LootingContainerItemEntry itemEntry)
    {
        if (HasRequiredQuest(player, itemEntry))
            return true;

        player.SendPacket(new SCLootItemFailedPacket(ErrorMessageType.NeedQuestToInteract, LootOwnerType, LootOwner.ObjId, itemEntry.ItemIndex, itemEntry.Item.TemplateId));
        return false;
    }

    /// <summary>
    /// Player manually closes the loot bag
    /// </summary>
    /// <param name="player"></param>
    /// <param name="itemIndex"></param>
    /// <param name="ownerType"></param>
    /// <param name="ownerObjId"></param>
    /// <param name="b"></param>
    public void CloseBag(Character player, ushort itemIndex, LootOwnerType ownerType, uint ownerObjId, byte b)
    {
        lock (ItemsLock)
            OpenedBy.Remove(player);
    }

    /// <summary>
    /// Apply a player roll to loot
    /// </summary>
    /// <param name="player"></param>
    /// <param name="itemIndex"></param>
    /// <param name="rollRequest"></param>
    public void DoPlayerRoll(Character player, ushort itemIndex, bool rollRequest)
    {
        lock (ItemsLock)
        {
            DoPlayerRollLocked(player, itemIndex, rollRequest);
        }
    }

    private void DoPlayerRollLocked(Character player, ushort itemIndex, bool rollRequest)
    {
        var itemEntry = Items.GetValueOrDefault(itemIndex);
        if (itemEntry == null || !itemEntry.RollInProgress || !IsWithinLootRange(player) ||
            !itemEntry.PlayerRolls.TryGetValue(player, out var previousRoll) || previousRoll != 0)
            return;

        var rollResult = rollRequest ? (sbyte)_random.Next(1, 101) : (sbyte)-1;
        itemEntry.PlayerRolls[player] = rollResult;

        // Notify the others of this roll result
        foreach (var targetPlayer in itemEntry.PlayerRolls.Keys)
        {
            targetPlayer.SendPacket(new SCLootDiceNotifyPacket(player.Name, itemEntry.Item, rollResult));
        }

        // Check if everybody has rolled
        if (itemEntry.PlayerRolls.Any(m => m.Value == 0))
            return;

        FinishRolling(itemEntry);
    }

    /// <summary>
    /// Handle the distribution when all rolls are finished
    /// </summary>
    /// <param name="itemEntry"></param>
    private void FinishRolling(LootingContainerItemEntry itemEntry)
    {
        if (!itemEntry.RollInProgress)
            return;

        itemEntry.RollInProgress = false;
        itemEntry.RollCompleted = true;
        var participants = itemEntry.PlayerRolls.Keys.ToArray();
        var round = itemEntry.PlayerRolls.ToDictionary();
        Character winner;
        while (true)
        {
            foreach (var participant in participants)
                participant.SendPacket(new SCLootDiceSummaryPacket(LootOwnerType, LootOwner.ObjId, itemEntry.ItemIndex, round));

            var highestResult = round.Values.Where(value => value > 0).DefaultIfEmpty().Max();
            var tied = round.Where(entry => entry.Value == highestResult && entry.Value > 0)
                .Select(entry => entry.Key).ToArray();
            if (tied.Length == 0)
            {
                // All players passed. A later pickup must not start the same roll again.
                itemEntry.PlayerRolls.Clear();
                return;
            }

            if (tied.Length == 1)
            {
                winner = tied[0];
                break;
            }

            // Re-roll only the tied highest Need players. Their original Need choice still applies.
            round = tied.ToDictionary(character => character, _ => (sbyte)_random.Next(1, 101));
            foreach (var (character, dice) in round)
            {
                itemEntry.PlayerRolls[character] = dice;
                foreach (var participant in participants)
                    participant.SendPacket(new SCLootDiceNotifyPacket(character.Name, itemEntry.Item, dice));
            }
        }

        itemEntry.HighestRoller = winner.Id;
        TryDistributeLootToPlayer(winner, itemEntry, false);
    }

    private sealed class LootRollTimeoutTask(LootingContainer container, LootingContainerItemEntry itemEntry) : AAEmu.Game.Models.Tasks.Task
    {
        public override void Execute()
        {
            lock (container.ItemsLock)
            {
                if (container.Items.GetValueOrDefault(itemEntry.ItemIndex) == itemEntry && itemEntry.RollInProgress)
                    container.ForceLootRollToFinish(itemEntry);
            }
        }
    }

    private bool TryDistributeLootToPlayer(Character player, LootingContainerItemEntry itemEntry, bool didLootAll)
    {
        if (!CanReceiveLoot(player) || !CanReceiveQuestItem(player, itemEntry) || !TryReserveLootItem(itemEntry))
            return false;

        var fullOldItemId = itemEntry.Item.Id;
        ulong allocatedItemId = 0;
        var grantSucceeded = false;
        var restored = false;
        try
        {
            var freeSpace = player.Inventory.Bag.SpaceLeftForItem(itemEntry.Item, out _);
            if (freeSpace < itemEntry.Item.Count)
            {
                RestoreLootItem(itemEntry);
                restored = true;
                player.SendPacket(new SCLootItemFailedPacket(ErrorMessageType.BagFull, LootOwnerType, LootOwner.ObjId, itemEntry.ItemIndex, itemEntry.Item.TemplateId));
                return false;
            }

            // var objId = (uint)(lootDropItem.Id >> 32);
            if (itemEntry.Item.TemplateId == Item.Coins)
            {
                if (!player.AddMoney(SlotType.Inventory, itemEntry.Item.Count))
                    return false;
                grantSucceeded = true;
            }
            else if (ItemManager.Instance.IsAutoEquipTradePack(itemEntry.Item.TemplateId))
            {
                // Auto-equip tradepack item branch.
                // Attempt to remove the current backpack item to free up the slot.
                if (player.Inventory.TakeoffBackpack(ItemTaskType.RecoverDoodadItem, true))
                {
                    itemEntry.Item.Id = ItemIdManager.Instance.GetNextId();
                    allocatedItemId = itemEntry.Item.Id;
                    // Try to add the new item to the Equipment container's Backpack slot.
                    if (!player.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.RecoverDoodadItem, itemEntry.Item, (int)EquipmentItemSlot.Backpack))
                    {
                        // If adding fails, release the item ID and restore original.
                        RestoreLootItem(itemEntry);
                        restored = true;
                        ItemIdManager.Instance.ReleaseId((uint)allocatedItemId);
                        allocatedItemId = 0;
                        itemEntry.Item.Id = fullOldItemId;
                        player.SendPacket(new SCLootItemFailedPacket(ErrorMessageType.BagFull, LootOwnerType, LootOwner.ObjId, itemEntry.ItemIndex, itemEntry.Item.TemplateId));
                        return false;
                    }
                    else
                    {
                        Logger.Trace("AutoEquipTradePack: Tradepack item added to Equipment container successfully.");
                        grantSucceeded = true;
                    }
                }
                else
                {
                    Logger.Warn("AutoEquipTradePack: Failed to take off backpack for auto-equip tradepack item TemplateId={0}.", itemEntry.Item.TemplateId);
                    RestoreLootItem(itemEntry);
                    restored = true;
                    player.SendPacket(new SCLootItemFailedPacket(ErrorMessageType.BagFull, LootOwnerType, LootOwner.ObjId, itemEntry.ItemIndex, itemEntry.Item.TemplateId));
                    return false;
                }
            }
            else
            {
                itemEntry.Item.Id = ItemIdManager.Instance.GetNextId();
                allocatedItemId = itemEntry.Item.Id;
                // Try to add the new item
                if (!player.Inventory.Bag.AcquireDefaultItem(didLootAll ? ItemTaskType.LootAll : ItemTaskType.Loot, itemEntry.Item.TemplateId, itemEntry.Item.Count, itemEntry.Item.Grade))
                {
                    // Free the Id again if failed
                    RestoreLootItem(itemEntry);
                    restored = true;
                    ItemIdManager.Instance.ReleaseId((uint)allocatedItemId);
                    allocatedItemId = 0;
                    // Re-assign the original loot bag id 
                    itemEntry.Item.Id = fullOldItemId;
                    // Send a bag full fail message
                    // player.SendErrorMessage(ErrorMessageType.BagFull);
                    player.SendPacket(new SCLootItemFailedPacket(ErrorMessageType.BagFull, LootOwnerType, LootOwner.ObjId, itemEntry.ItemIndex, itemEntry.Item.TemplateId));
                    return false;
                }
                grantSucceeded = true;
            }

            if (itemEntry.Item.Count > 0)
            {
                player.Achievements?.Increment(
                    CharRecordKind.GetLootitem,
                    itemEntry.Item.TemplateId,
                    0,
                    (uint)itemEntry.Item.Count);
            }

            // TODO: check what packet this sends to others
            player.SendPacket(new SCLootItemTookPacket(itemEntry.Item.TemplateId, itemEntry.ItemIndex, LootOwnerType, LootOwner.ObjId, itemEntry.Item.Count));

            if (Items.Count <= 0)
                UpdateLootState();
            return true;
        }
        finally
        {
            if (!grantSucceeded && allocatedItemId != 0)
            {
                ItemIdManager.Instance.ReleaseId((uint)allocatedItemId);
                itemEntry.Item.Id = fullOldItemId;
            }

            if (!grantSucceeded && !restored)
            {
                itemEntry.Item.Id = fullOldItemId;
                RestoreLootItem(itemEntry);
            }
        }
    }

    private bool TryReserveLootItem(LootingContainerItemEntry itemEntry)
    {
        lock (ItemsLock)
        {
            if (!Items.TryGetValue(itemEntry.ItemIndex, out var currentItemEntry))
                return false;

            if (currentItemEntry != itemEntry)
                return false;

            Items.Remove(itemEntry.ItemIndex);
            return true;
        }
    }

    private void RestoreLootItem(LootingContainerItemEntry itemEntry)
    {
        lock (ItemsLock)
        {
            Items.TryAdd(itemEntry.ItemIndex, itemEntry);
        }
    }

    /// <summary>
    /// Forces any ongoing loot rolls to end by making the remaining players auto-pass
    /// </summary>
    private void ForceLootRollToFinish(LootingContainerItemEntry itemEntry)
    {
        if (!itemEntry.RollInProgress)
            return;

        foreach (var player in itemEntry.PlayerRolls.Where(entry => entry.Value == 0).Select(entry => entry.Key).ToArray())
        {
            itemEntry.PlayerRolls[player] = -1;
            foreach (var participant in itemEntry.PlayerRolls.Keys)
                participant.SendPacket(new SCLootDiceNotifyPacket(player.Name, itemEntry.Item, -1));
        }

        FinishRolling(itemEntry);
    }

    public void MakeLootPublic()
    {
        lock (ItemsLock)
        {
            if (TeamLootingRule == null)
                return;

            foreach (var itemEntry in Items.Values.ToArray())
                ForceLootRollToFinish(itemEntry);

            if (Items.Count <= 0)
                return;

            // Claimed items remain reserved for their winner, including a failed inventory grant.
            TeamLootingRule.LootMethod = LootingRuleMethod.Public;
            TeamLootingRule.MinimumGrade = 0;
            TeamLootingRule.RollForBindOnPickup = false;

            LootOwner?.BroadcastPacket(new SCLootableStatePacket(LootOwnerType, LootOwner.ObjId, true), false);
        }
    }

    public bool CanMakePublic()
    {
        lock (ItemsLock)
        {
            return TeamLootingRule != null &&
                   TeamLootingRule.LootMethod != LootingRuleMethod.Public &&
                   Items.Count > 0 &&
                   CreationTime > DateTime.MinValue &&
                   CreationTime.AddSeconds(MakeLootPublicTime) <= DateTime.UtcNow;
        }
    }
}
