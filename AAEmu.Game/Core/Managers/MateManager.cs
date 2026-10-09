using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Names;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class MateManager(WorldInstance parentWorldInstance)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, List<Mate>> _activeMates = []; // ownerId, Mount
    private readonly HashSet<Mate> _matesBeingRemoved = [];
    private readonly object _activeMatesLock = new();

    private WorldInstance World { get; init; } = parentWorldInstance;

    /// <summary>
    /// Gets active pets
    /// </summary>
    /// <param name="ownerId"></param>
    /// <returns></returns>
    public List<Mate> GetActiveMates(uint ownerId)
    {
        lock (_activeMatesLock)
            return _activeMates.TryGetValue(ownerId, out var mates) ? [.. mates] : [];
    }

    // The caller holds the persistence lock. Keep identity validation and the
    // field copy together so removal cannot retire or replace a captured mate.
    internal void CaptureOwnedPersistentMateStates(Character owner, Action<Mate> capture)
    {
        if (owner == null || !ReferenceEquals(owner.ParentWorld, World))
            return;
        lock (_activeMatesLock)
        {
            if (!_activeMates.TryGetValue(owner.Id, out var mates))
                return;
            foreach (var mate in mates)
            {
                if (mate.IsTemporarySummon || mate.ItemId == 0 || mate.OwnerId != owner.Id ||
                    mate.OwnerObjId != owner.ObjId || !ReferenceEquals(mate.ParentWorld, World) ||
                    _matesBeingRemoved.Contains(mate))
                    continue;
                capture(mate);
            }
        }
    }

    /// <summary>
    /// Gets an active pet by it's TlId
    /// </summary>
    /// <param name="tlId"></param>
    /// <returns></returns>
    public Mate GetActiveMateByTlId(uint tlId)
    {
        lock (_activeMatesLock)
            return _activeMates.Values.SelectMany(mateList => mateList).FirstOrDefault(mate => mate.TlId == tlId);
    }

    public Mate GetActiveMateByTlId(uint ownerId, uint tlId)
    {
        lock (_activeMatesLock)
            return _activeMates.GetValueOrDefault(ownerId)?.FirstOrDefault(mate => mate.TlId == tlId);
    }

    internal bool IsOwnedMate(Character owner, Mate mate)
    {
        if (owner == null || mate == null || mate.OwnerObjId != owner.ObjId)
            return false;
        lock (_activeMatesLock)
            return _activeMates.TryGetValue(owner.Id, out var mates) && mates.Contains(mate) &&
                !_matesBeingRemoved.Contains(mate);
    }

    internal Mate GetOwnedMate(Character owner, uint tlId)
    {
        if (owner == null)
            return null;
        var mate = GetActiveMateByTlId(owner.Id, tlId);
        return IsOwnedMate(owner, mate) ? mate : null;
    }

    /// <summary>
    /// Gets an active pet by it's ObjId
    /// </summary>
    /// <param name="mateObjId"></param>
    /// <returns></returns>
    public Mate GetActiveMateByMateObjId(uint mateObjId)
    {
        lock (_activeMatesLock)
            return _activeMates.Values.SelectMany(mateList => mateList).FirstOrDefault(mate => mate.ObjId == mateObjId);
    }

    /// <summary>
    /// Checks if a ObjId is mounted on any of the pets and returns which seat
    /// </summary>
    /// <param name="objId"></param>
    /// <param name="attachPoint"></param>
    /// <returns></returns>
    public Mate GetIsMounted(uint objId, out AttachPointKind attachPoint)
    {
        attachPoint = AttachPointKind.System;
        List<Mate> mates;
        lock (_activeMatesLock)
            mates = [.. _activeMates.Values.SelectMany(mateList => mateList)];
        foreach (var mate in mates)
            foreach (var ati in mate.Passengers.Where(ati => ati.Value._objId == objId))
            {
                attachPoint = ati.Key;
                return mate;
            }

        return null;
    }

    /// <summary>
    /// Change the state of a pet
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="tlId"></param>
    /// <param name="newState"></param>
    public void ChangeStateMate(GameConnection connection, uint tlId, byte newState)
    {
        var mate = GetOwnedMate(connection.ActiveChar, tlId);
        if (mate != null)
            mate.UserState = newState;
    }

    /// <summary>
    /// Changes the current target of a pet 
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="tlId"></param>
    /// <param name="objId"></param>
    public void ChangeTargetMate(GameConnection connection, uint tlId, uint objId)
    {
        var mateInfo = GetOwnedMate(connection.ActiveChar, tlId);
        if (mateInfo == null) return;
        var target = objId > 0 ? World.GetUnit(objId) : null;
        if (objId > 0 && (target == null || !ReferenceEquals(target.ParentWorld, mateInfo.ParentWorld) ||
            target.Transform.InstanceId != mateInfo.Transform.InstanceId))
            return;
        mateInfo.CurrentTarget = target;
        mateInfo.BroadcastPacket(new SCTargetChangedPacket(mateInfo.ObjId, mateInfo.CurrentTarget?.ObjId ?? 0), true);

        Logger.Debug($"ChangeTargetMate. tlId: {mateInfo.TlId}, objId: {mateInfo.ObjId}, targetObjId: {objId}");
    }

    /// <summary>
    /// Renames a pet
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="tlId"></param>
    /// <param name="newName"></param>
    /// <returns></returns>
    public Mate RenameMount(GameConnection connection, uint tlId, string newName)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            var mateInfo = GetOwnedMate(connection?.ActiveChar, tlId);
            if (mateInfo == null || !NameRules.IsWellFormed(newName) || newName[0] == ' ' || newName[^1] == ' ')
                return null;
            var normalizedName = newName.NormalizeName();
            if (NameRules.Validate(normalizedName, NameType.Summon) != NameValidationResult.Valid)
                return null;
            mateInfo.Name = normalizedName;
            mateInfo.BroadcastPacket(new SCUnitNameChangedPacket(mateInfo.ObjId, normalizedName), false);
            return mateInfo;
        }
    }

    /// <summary>
    /// Mounts the active character of a connection on target mount by its TlId
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="tlId"></param>
    /// <param name="attachPoint"></param>
    /// <param name="reason"></param>
    public void MountMate(GameConnection connection, uint tlId, AttachPointKind attachPoint, AttachUnitReason reason)
    {
        lock (SaveManager.PersistenceSyncRoot)
            MountMateCore(connection, tlId, attachPoint, reason);
    }

    private void MountMateCore(GameConnection connection, uint tlId, AttachPointKind attachPoint, AttachUnitReason reason)
    {
        var character = connection.ActiveChar;
        var mateInfo = GetActiveMateByTlId(tlId);
        if (mateInfo == null || character == null)
            return;

        lock (character.AttachmentSyncRoot)
        lock (mateInfo.AttachmentSyncRoot)
        {
            if (!ReferenceEquals(GetActiveMateByTlId(tlId), mateInfo) ||
                !MountSeatAuthorization.CanEnter(character, mateInfo, mateInfo.Transform.World.Position,
                    MountSeatAuthorization.MateRange) ||
                !MateSeatGameData.Instance.HasSeat(mateInfo.ModelId, attachPoint) ||
                !mateInfo.Passengers.TryGetValue(attachPoint, out var seatInfo) || seatInfo._objId != 0 ||
                (attachPoint == AttachPointKind.Driver && !IsOwnedMate(character, mateInfo)))
                return;

            seatInfo._objId = character.ObjId;
            seatInfo._reason = reason;
            character.Transform.StickyParent = null;
            character.Transform.Parent = mateInfo.Transform;
            character.Transform.Local.SetPosition(0, 0, 0);
            character.IsRiding = true;
            character.AttachedPoint = attachPoint;
            character.IsVisible = true;
        }
        if (attachPoint == AttachPointKind.Driver)
            mateInfo.ResetRidingMovement();
        character.BroadcastPacket(new SCUnitAttachedPacket(character.ObjId, attachPoint, reason, mateInfo.ObjId), true);
        character.Buffs.TriggerRemoveOn(BuffRemoveOn.Mount);
    }

    /// <summary>
    /// Unmounts a character from target mount using its TlId
    /// </summary>
    /// <param name="character"></param>
    /// <param name="tlId"></param>
    /// <param name="attachPoint"></param>
    /// <param name="reason"></param>
    public void UnMountMate(Character character, uint tlId, AttachPointKind attachPoint, AttachUnitReason reason)
    {
        var mateInfo = GetActiveMateByTlId(tlId);
        if (mateInfo == null)
            return;

        UnMountMate(character, mateInfo, attachPoint, reason, checkRequest: true);
    }

    internal bool CanRequestUnmount(Character actor, Mate mate, AttachPointKind seat)
    {
        if (actor == null || mate == null || !ReferenceEquals(actor.ParentWorld, mate.ParentWorld) ||
            actor.Transform.InstanceId != mate.Transform.InstanceId ||
            !mate.Passengers.TryGetValue(seat, out var passenger) || passenger._objId == 0)
            return false;

        // The client allows an occupant to leave and the owner to remove a passenger.
        return (passenger._objId == actor.ObjId && actor.AttachedPoint == seat &&
                MountSeatAuthorization.IsAttached(actor, mate)) ||
            (seat == AttachPointKind.Passenger0 && IsOwnedMate(actor, mate));
    }

    private void UnMountMate(Character actor, Mate mate, AttachPointKind seat, AttachUnitReason reason,
        bool checkRequest = false)
    {
        Character occupant;
        uint occupantId;
        lock (mate.AttachmentSyncRoot)
        {
            if (!mate.Passengers.TryGetValue(seat, out var passenger) || passenger._objId == 0 ||
                checkRequest && !CanRequestUnmount(actor, mate, seat))
                return;
            occupantId = passenger._objId;
            occupant = World?.GetUnit(occupantId) as Character;
        }

        if (occupant == null)
        {
            lock (mate.AttachmentSyncRoot)
                if (mate.Passengers[seat]._objId == occupantId)
                    mate.Passengers[seat]._objId = 0;
            return;
        }

        lock (occupant.AttachmentSyncRoot)
        lock (mate.AttachmentSyncRoot)
        {
            var passenger = mate.Passengers[seat];
            if (passenger._objId != occupantId || checkRequest && !CanRequestUnmount(actor, mate, seat))
                return;
            passenger._objId = 0;
            passenger._reason = reason;
            if (!IsCurrentOccupant(occupant, mate, seat))
                return;

            occupant.Transform.Parent = null;
            var position = mate.Transform.World;
            occupant.SetPosition(position.Position.X, position.Position.Y, position.Position.Z,
                position.Rotation.X, position.Rotation.Y, position.Rotation.Z);
            occupant.IsRiding = false;
            occupant.AttachedPoint = AttachPointKind.None;
        }

        mate.StopUpdateXp();
        if (seat == AttachPointKind.Driver)
            mate.ResetRidingMovement();
        occupant.BroadcastPacket(new SCUnitDetachedPacket(occupant.ObjId, reason), true);
        occupant.Events.OnUnmount(actor, new OnUnmountArgs());
        mate.Buffs.TriggerRemoveOn(BuffRemoveOn.Unmount);
        occupant.Buffs.TriggerRemoveOn(BuffRemoveOn.Unmount);
    }

    internal static bool IsCurrentOccupant(Character occupant, Mate mate, AttachPointKind seat) =>
        ReferenceEquals(occupant.ParentWorld, mate.ParentWorld) &&
        occupant.Transform.InstanceId == mate.Transform.InstanceId && occupant.AttachedPoint == seat &&
        MountSeatAuthorization.IsAttached(occupant, mate);

    /// <summary>
    /// Adds a new pet (or despawns the previous one)
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="mate"></param>
    /// <param name="item"></param>
    public void AddActiveMateAndSpawn(Character owner, Mate mate, Item item)
    {
        if (!ZoneSkillRestrictions.CanUseItem(owner, item, mate.Transform.World.Position))
        {
            DespawnReservedMate(owner, mate);
            return;
        }
        Mate existingMate;
        lock (_activeMatesLock)
        {
            // Temporary skill summons coexist with the player's one persistent mount or battle pet.
            existingMate = _activeMates.GetValueOrDefault(owner.Id)?.FirstOrDefault(active => !active.IsTemporarySummon);
            if (existingMate is null)
                TrackActiveMateUnsafe(owner.Id, mate, prioritize: true);
        }

        if (existingMate is not null)
        {
            // SpawnMount constructs and reserves IDs for the incoming mate before this toggle
            // check. Retire that never-tracked instance before despawning the existing mate.
            DespawnReservedMate(owner, mate);
            owner.Mates.DespawnMate(existingMate.TlId);
            return;
        }

        if (!CanSpawnTrackedMate(owner.IsOnline, owner.Hp, mate.IsTemporarySummon, mate.DespawnOnCreatorDeath))
        {
            RemoveActiveMateAndDespawn(owner, mate);
            return;
        }

        if (TrySpawnTrackedMate(owner, mate, () =>
            {
                owner.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.UpdateSummonMateItem, [new ItemUpdate(item)], [])); // TODO - maybe update details
                owner.SendPacket(new SCMateSpawnedPacket(mate));
                Thread.Sleep(50);
            }, mate.Spawn))
            Logger.Debug($"Mount spawned. ownerObjId: {owner.ObjId}, tlId: {mate.TlId}, mateObjId: {mate.ObjId}");
    }

    /// <summary>
    /// Spawns a short-lived skill summon without replacing or persisting the player's normal mate.
    /// </summary>
    public void AddTemporaryMateAndSpawn(Character owner, Mate mate)
    {
        var replacedMates = TrackTemporaryMate(owner.Id, mate);
        try
        {
            if (!CanSpawnTrackedMate(owner.IsOnline, owner.Hp, mate.IsTemporarySummon, mate.DespawnOnCreatorDeath))
            {
                RemoveActiveMateAndDespawn(owner, mate);
                return;
            }

            if (TrySpawnTrackedMate(owner, mate,
                    () => owner.SendPacket(new SCMateSpawnedPacket(mate)),
                    () =>
                    {
                        mate.Spawn();
                        var currentTarget = mate.CurrentTarget;
                        if (currentTarget is not null)
                            mate.BroadcastPacket(new SCTargetChangedPacket(mate.ObjId, currentTarget.ObjId), true);
                    }))
                Logger.Debug($"Temporary mate spawned. ownerObjId: {owner.ObjId}, tlId: {mate.TlId}, mateObjId: {mate.ObjId}");
        }
        finally
        {
            foreach (var replacedMate in replacedMates)
                DespawnReservedMate(owner, replacedMate);
        }
    }

    /// <summary>
    /// Despawns and Removes a pet
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="tlId"></param>
    public void RemoveActiveMateAndDespawn(Character owner, uint tlId)
    {
        Mate mateInfo;
        lock (_activeMatesLock)
        {
            mateInfo = _activeMates.GetValueOrDefault(owner.Id)?.FirstOrDefault(mate => mate.TlId == tlId);
        }

        RemoveActiveMateAndDespawn(owner, mateInfo);
    }

    /// <summary>
    /// Removes the exact tracked mate instance. Identity checking prevents a delayed temporary
    /// summon task from acting on a different mate after its recyclable object IDs are reused.
    /// </summary>
    public void RemoveActiveMateAndDespawn(Character owner, Mate mateInfo)
    {
        if (owner is null || mateInfo is null || !TryBeginMateRemoval(owner.Id, mateInfo))
            return;

        DespawnReservedMate(owner, mateInfo);
    }

    private void DespawnReservedMate(Character owner, Mate mateInfo)
    {
        var removed = false;
        try
        {
            removed = mateInfo.TryRunDespawnLifecycle(shouldDeleteWorldObject =>
            {
                try
                {
                    if (shouldDeleteWorldObject)
                    {
                        foreach (var ati in mateInfo.Passengers)
                            UnMountMate(WorldManager.Instance.GetCharacterByObjId(ati.Value._objId), mateInfo, ati.Key, AttachUnitReason.SlaveBinding);
                    }
                }
                finally
                {
                    try
                    {
                        if (shouldDeleteWorldObject)
                            mateInfo.Delete();
                    }
                    finally
                    {
                        try
                        {
                            ObjectIdManager.Instance.ReleaseId(mateInfo.ObjId);
                        }
                        finally
                        {
                            TlIdManager.Instance.ReleaseId(mateInfo.TlId);
                        }
                    }
                }
            });
        }
        finally
        {
            CompleteMateRemoval(mateInfo);
        }

        if (removed)
            Logger.Debug($"Mount removed. ownerObjId: {owner.ObjId}, tlId: {mateInfo.TlId}, mateObjId: {mateInfo.ObjId}");
    }

    private bool TrySpawnTrackedMate(Character owner, Mate mate, Action beforeWorldSpawnAction, Action worldSpawnAction)
    {
        try
        {
            return mate.TryRunSpawnLifecycle(beforeWorldSpawnAction, worldSpawnAction);
        }
        catch
        {
            // A concurrent replacement, death, or logout may already own the reservation.
            // The mate lifecycle still guarantees that cleanup and ID release run only once.
            TryBeginMateRemoval(owner.Id, mate);
            DespawnReservedMate(owner, mate);
            throw;
        }
    }

    internal static bool CanSpawnTrackedMate(
        bool ownerIsOnline,
        int ownerHp,
        bool isTemporarySummon,
        bool despawnOnCreatorDeath)
    {
        return ownerIsOnline &&
            ((isTemporarySummon && !despawnOnCreatorDeath) || ownerHp > 0);
    }

    internal List<Mate> TrackTemporaryMate(uint ownerId, Mate mate)
    {
        mate.IsTemporarySummon = true;
        lock (_activeMatesLock)
        {
            var replacements = _activeMates.GetValueOrDefault(ownerId)?
                .Where(active => active.IsTemporarySummon && active.TemplateId == mate.TemplateId)
                .ToList() ?? [];
            var reservedReplacements = new List<Mate>(replacements.Count);
            foreach (var replacement in replacements)
                if (TryBeginMateRemovalUnsafe(ownerId, replacement))
                    reservedReplacements.Add(replacement);

            TrackActiveMateUnsafe(ownerId, mate, prioritize: false);
            return reservedReplacements;
        }
    }

    internal void TrackActiveMate(uint ownerId, Mate mate, bool prioritize = false)
    {
        lock (_activeMatesLock)
            TrackActiveMateUnsafe(ownerId, mate, prioritize);
    }

    private void TrackActiveMateUnsafe(uint ownerId, Mate mate, bool prioritize)
    {
        if (!_activeMates.TryGetValue(ownerId, out var mates))
        {
            mates = [];
            _activeMates.Add(ownerId, mates);
        }

        if (prioritize)
            mates.Insert(0, mate);
        else
            mates.Add(mate);
    }

    internal bool TryBeginMateRemoval(uint ownerId, Mate expectedMate)
    {
        lock (_activeMatesLock)
            return TryBeginMateRemovalUnsafe(ownerId, expectedMate);
    }

    private bool TryBeginMateRemovalUnsafe(uint ownerId, Mate expectedMate)
    {
        if (_matesBeingRemoved.Contains(expectedMate) ||
            !_activeMates.TryGetValue(ownerId, out var mates))
            return false;

        var mateIndex = mates.FindIndex(mate => ReferenceEquals(mate, expectedMate));
        if (mateIndex < 0)
            return false;

        mates.RemoveAt(mateIndex);
        _matesBeingRemoved.Add(expectedMate);
        if (mates.Count == 0)
            _activeMates.Remove(ownerId);
        return true;
    }

    internal void CompleteMateRemoval(Mate mate)
    {
        lock (_activeMatesLock)
            _matesBeingRemoved.Remove(mate);
    }

    internal List<Mate> GetMatesToRemoveOnOwnerDeath(uint ownerId)
    {
        lock (_activeMatesLock)
            return _activeMates.GetValueOrDefault(ownerId)?
                .Where(mate => !mate.IsTemporarySummon || mate.DespawnOnCreatorDeath)
                .ToList() ?? [];
    }

    public void RemoveActiveMatesOnOwnerDeath(Character character)
    {
        if (character is null)
            return;

        foreach (var mate in GetMatesToRemoveOnOwnerDeath(character.Id))
            character.Mates.DespawnMate(mate);
    }

    /// <summary>
    /// Remove all mounts that are in the world and owned by character
    /// </summary>
    /// <param name="character"></param>
    public void RemoveAndDespawnAllActiveOwnedMates(Character character)
    {
        if (character == null) return;
        foreach (var mate in GetActiveMates(character.Id))
            character.Mates.DespawnMate(mate);
    }

    /// <summary>
    /// Load pet related data from DB
    /// </summary>
    public void Load()
    {
        lock (_activeMatesLock)
        {
            _activeMates = [];
            _matesBeingRemoved.Clear();
        }
    }
}
