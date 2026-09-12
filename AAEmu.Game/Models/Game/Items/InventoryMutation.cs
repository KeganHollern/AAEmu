using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Items;

/// <summary>
/// Prepares related inventory and wallet changes without invoking observable callbacks.
/// The caller must hold PersistenceSyncRoot until disposal. Complete accepts the prepared
/// state and publishes its notifications; disposal otherwise restores the original state.
/// This class does not persist a settlement.
/// </summary>
public sealed class InventoryMutation : IDisposable
{
    private readonly ItemTaskType _taskType;
    private readonly Dictionary<ItemContainer, ContainerState> _containers = [];
    private readonly Dictionary<Item, ItemState> _items = [];
    private readonly Dictionary<Character, (long Money, long Bank)> _wallets = [];
    private readonly List<Item> _created = [];
    private readonly List<Item> _removed = [];
    private readonly HashSet<Item> _moved = [];
    private readonly Dictionary<ICharacter, List<ItemTask>> _tasks = [];
    private readonly List<Action> _notifications = [];
    private bool _failed;
    private bool _finished;

    public InventoryMutation(ItemTaskType taskType)
    {
        RequireLock();
        _taskType = taskType;
    }

    public IReadOnlyList<Item> RemovedItems => _removed;

    public bool TryChangeMoney(Character character, int delta, SlotType location = SlotType.Inventory)
    {
        RequireActive();
        if (_failed || character == null || location is not (SlotType.Inventory or SlotType.Bank))
            return Fail();

        long balance;
        try
        {
            balance = checked((location == SlotType.Inventory ? character.Money : character.Money2) + delta);
        }
        catch (OverflowException)
        {
            return Fail();
        }
        if (balance < 0 || (delta < 0 && location == SlotType.Inventory &&
            balance < TradeReservation.GetReservedMoney(character)))
            return Fail();

        _wallets.TryAdd(character, (character.Money, character.Money2));
        if (location == SlotType.Inventory)
        {
            character.Money = balance;
            AddTask(character, new MoneyChange(delta));
        }
        else
        {
            character.Money2 = balance;
            AddTask(character, new MoneyChangeBank(delta));
        }
        return true;
    }

    public bool TryChangeGrade(ItemContainer source, Item item, byte grade)
    {
        RequireActive();
        if (_failed || !IsHeldBy(item, source) || TradeReservation.GetReservedCount(item) != 0)
            return Fail();
        Capture(source);
        Capture(item);
        item.Grade = grade;
        AddTask(source.Owner, new ItemGradeChange(item, grade));
        return true;
    }

    public bool TryConsume(ItemContainer source, Item item, int count)
    {
        RequireActive();
        if (_failed || _moved.Contains(item) || !IsHeldBy(item, source) || count <= 0 ||
            count > item.Count - TradeReservation.GetReservedCount(item) ||
            (count == item.Count && !item.CanDestroy()))
            return Fail();

        Capture(source);
        Capture(item);
        var owner = source.Owner;
        var slot = (byte)item.Slot;
        var entireStack = count == item.Count;
        if (entireStack)
        {
            AddTask(owner, new ItemRemoveSlot(item.Id, item.SlotType, slot));
            if (item.ExpirationOnlineMinutesLeft > 0 || item.ExpirationTime > DateTime.UtcNow ||
                item.UnpackTime > DateTime.UtcNow)
            {
                var expiration = new SCSyncItemLifespanPacket(false, item.Id, item.TemplateId, DateTime.MinValue);
                _notifications.Add(() => owner?.SendPacket(expiration));
            }
            source.Items.Remove(item);
            item._holdingContainer = null;
            _removed.Add(item);
        }
        else
        {
            AddTask(owner, new ItemCountUpdate(item, -count));
        }
        item.Count -= count;
        source.UpdateFreeSlotCount();
        _notifications.Add(() => owner?.Inventory.OnConsumedItem(item, count));
        if (entireStack)
            _notifications.Add(() => source.OnLeaveContainer(item, null, slot));
        return true;
    }

    /// <summary>Moves the exact item, retaining its identity and all item-specific details.</summary>
    public bool TryMove(Item item, ItemContainer destination, int preferredSlot = -1)
    {
        RequireActive();
        var source = item?._holdingContainer;
        if (_failed || destination == null || !IsHeldBy(item, source) || source == destination ||
            TradeReservation.GetReservedCount(item) != 0)
            return Fail();
        return Move(item, source, destination, preferredSlot, false);
    }

    /// <summary>Prepares all outgoing quantities before placing incoming items, including a full-bag exchange.</summary>
    public bool TryExchange(IReadOnlyList<(Item Item, int Count, ItemContainer Destination)> transfers)
    {
        RequireActive();
        if (_failed || transfers == null)
            return Fail();
        var ids = new HashSet<ulong>();
        foreach (var (item, count, destination) in transfers)
        {
            if (!IsHeldBy(item, item?._holdingContainer) || destination == null ||
                item._holdingContainer == destination || count <= 0 || count > item.Count ||
                item.Template == null || item.Count > item.Template.MaxCount ||
                !ids.Add(item.Id) || _moved.Contains(item) || TradeReservation.GetReservedCount(item) != 0)
                return Fail();
        }

        var moving = new List<(Item Item, ItemContainer Source, ItemContainer Destination, bool Created)>();
        foreach (var (item, count, destination) in transfers)
        {
            var source = item._holdingContainer;
            Capture(source);
            Capture(item);
            if (count == item.Count)
            {
                if (source.ContainerType != SlotType.Mail)
                    AddTask(source.Owner, new ItemRemoveSlot(item.Id, item.SlotType, (byte)item.Slot));
                source.Items.Remove(item);
                source.UpdateFreeSlotCount();
                moving.Add((item, source, destination, false));
            }
            else
            {
                var id = ItemManager.Instance.ReserveItemId();
                if (id == 0)
                    return Fail();
                var split = item.CopyForSplit(id, count);
                if (!ItemManager.Instance.AddItem(split))
                    return Fail();
                _created.Add(split);
                item.Count -= count;
                _moved.Add(item);
                AddTask(source.Owner, new ItemCountUpdate(item, -count));
                var owner = source.Owner;
                _notifications.Add(() => owner?.Inventory.OnConsumedItem(item, count));
                moving.Add((split, null, destination, true));
            }
        }
        foreach (var (item, source, destination, created) in moving)
            if (!Move(item, source, destination, -1, created, source != null))
                return false;
        return true;
    }

    /// <summary>
    /// Adopts a newly created, registered item. Its id is released if this mutation fails.
    /// Callers can prepare item-specific details before passing it here.
    /// </summary>
    public bool TryAddCreated(Item item, ItemContainer destination, int preferredSlot = -1)
    {
        RequireActive();
        if (item == null || item.Id == 0 || item._holdingContainer != null || _created.Contains(item))
            return Fail();
        _created.Add(item);
        if (_failed || destination == null || item.Count <= 0)
            return Fail();
        return Move(item, null, destination, preferredSlot, true);
    }

    public bool TryGrant(ItemContainer destination, uint templateId, int count, int grade = -1)
    {
        return TryGrant(destination, templateId, count, out _, grade);
    }

    public bool TryGrant(ItemContainer destination, uint templateId, int count,
        out IReadOnlyList<Item> grantedItems, int grade = -1)
    {
        RequireActive();
        var granted = new List<Item>();
        grantedItems = granted;
        if (_failed || destination == null || count <= 0)
            return Fail();
        var template = ItemManager.Instance.GetTemplate(templateId);
        if (template == null || template.MaxCount <= 0)
            return Fail();
        if (template.FixedGrade >= 0 && (!template.Gradable || grade < 0))
            grade = template.FixedGrade;
        if (grade < 0)
            grade = 0;
        if (grade > byte.MaxValue)
            return Fail();

        Capture(destination);
        // Mail and auction records refer to exact attachment ids, so never merge their stacks.
        if (destination.ContainerType is not (SlotType.Mail or SlotType.Auction))
        {
            foreach (var item in destination.Items.Where(item => item.TemplateId == templateId &&
                         item.Grade == grade).OrderBy(item => item.Slot).ToArray())
            {
                if (!IsHeldBy(item, destination) || item.Count > template.MaxCount)
                    return Fail();
                var added = Math.Min(template.MaxCount - item.Count, count);
                if (added == 0)
                    continue;
                Capture(item);
                item.Count += added;
                count -= added;
                granted.Add(item);
                AddTask(destination.Owner, new ItemCountUpdate(item, added));
                var owner = destination.Owner;
                _notifications.Add(() => owner?.Inventory.OnAcquiredItem(item, added, true));
                if (count == 0)
                    return true;
            }
        }

        while (count > 0)
        {
            var added = Math.Min(count, template.MaxCount);
            var item = ItemManager.Instance.Create(templateId, added, (byte)grade);
            if (item == null)
                return Fail();
            var owner = destination.Owner;
            var sync = new List<GamePacket>();
            if (template.ExpAbsLifetime > 0)
                sync.Add(ItemManager.SetItemExpirationTime(item, DateTime.UtcNow.AddMinutes(template.ExpAbsLifetime)));
            if (template.ExpOnlineLifetime > 0)
                sync.Add(ItemManager.SetItemOnlineExpirationTime(item, template.ExpOnlineLifetime));
            if (template.ExpDate > DateTime.MinValue)
                sync.Add(ItemManager.SetItemExpirationTime(item, template.ExpDate));
            if (item is EquipItem && template is EquipItemTemplate equipment)
            {
                item.ChargeCount = equipment.ChargeCount;
                if (equipment.ChargeLifetime > 0 && !equipment.BindType.HasFlag(ItemBindType.BindOnUnpack))
                    item.ChargeStartTime = DateTime.UtcNow;
            }
            if (!TryAddCreated(item, destination))
                return false;
            granted.Add(item);
            count -= added;
            foreach (var packet in sync)
                _notifications.Add(() => owner?.SendPacket(packet));
        }
        return true;
    }

    public IReadOnlyList<ItemTask> GetTasks(ICharacter character)
    {
        RequireLock();
        return _tasks.TryGetValue(character, out var tasks) ? tasks.AsReadOnly() : [];
    }

    public bool Complete(bool publishItemTasks = true)
    {
        RequireActive();
        if (_failed)
            return false;

        // Once accepted, a throwing observer must never restore assets already observed by others.
        _finished = true;
        foreach (var item in _removed)
            ItemManager.Instance.ReleaseId(item.Id);
        foreach (var (owner, tasks) in _tasks)
        {
            if (publishItemTasks && _taskType != ItemTaskType.Invalid && tasks.Count > 0)
                foreach (var batch in tasks.Chunk(30))
                    owner.SendPacket(new SCItemTaskSuccessPacket(_taskType, [.. batch], []));
        }
        foreach (var character in _wallets.Keys)
            character.UpdateGoldAchievement();
        foreach (var notification in _notifications)
            notification();
        return true;
    }

    /// <summary>
    /// Retains prepared state without publishing when a commit outcome is uncertain.
    /// It is also safe after Complete, including when a notification threw.
    /// </summary>
    public void PreservePreparedState()
    {
        RequireLock();
        _finished = true;
    }

    public void Dispose()
    {
        RequireLock();
        if (_finished)
            return;
        _finished = true;
        foreach (var (character, wallet) in _wallets)
        {
            character.Money = wallet.Money;
            character.Money2 = wallet.Bank;
        }
        foreach (var state in _items.Values)
            state.Restore();
        foreach (var state in _containers.Values)
            state.Restore();
        foreach (var item in _created)
        {
            item._holdingContainer = null;
            ItemManager.Instance.ReleaseId(item.Id);
        }
    }

    private bool Move(Item item, ItemContainer source, ItemContainer destination, int preferredSlot, bool created,
        bool sourceDetached = false)
    {
        // Container callbacks describe one transition. Do not queue intermediate
        // transitions whose item slot could change again before publication.
        if (!_moved.Add(item))
            return Fail();
        var slot = FindSlot(destination, item, preferredSlot);
        if (slot < 0 || !destination.CanAccept(item, slot))
            return Fail();
        Capture(destination);
        Capture(item);
        var oldSlot = (byte)item.Slot;
        var oldSlotType = item.SlotType;
        var oldOwnerId = item.OwnerId;
        var sourceOwner = source?.Owner;
        var destinationOwner = destination.Owner;
        if (source != null)
        {
            Capture(source);
            source.Items.Remove(item);
            source.UpdateFreeSlotCount();
            if (!sourceDetached && source.ContainerType != SlotType.Mail)
                AddTask(sourceOwner, new ItemRemoveSlot(item.Id, oldSlotType, oldSlot));
        }
        item._holdingContainer = destination;
        item.SlotType = destination.ContainerType;
        item.Slot = slot;
        item.OwnerId = destination.OwnerId;
        if ((destination.ContainerType == SlotType.Inventory && item.Template.BindType == ItemBindType.BindOnPickup) ||
            (destination.ContainerType == SlotType.Equipment && item.Template.BindType == ItemBindType.BindOnEquip))
            item.SetFlag(ItemFlag.SoulBound);
        destination.Items.Insert(0, item);
        destination.UpdateFreeSlotCount();
        if (destination.ContainerType != SlotType.None)
            AddTask(destinationOwner, new ItemAdd(item));
        if (source != null)
            _notifications.Add(() => source.OnLeaveContainer(item, destination, oldSlot));
        _notifications.Add(() => destination.OnEnterContainer(item, source, oldSlot));
        var amount = item.Count;
        if (destination.ContainerType != SlotType.Mail &&
            (created || oldOwnerId != destination.OwnerId || oldSlotType == SlotType.Mail))
            _notifications.Add(() => destinationOwner?.Inventory.OnAcquiredItem(item, amount));
        if (source != null && oldSlotType != SlotType.Mail &&
            (destination.ContainerType == SlotType.Mail || oldOwnerId != destination.OwnerId))
            _notifications.Add(() => sourceOwner?.Inventory.OnConsumedItem(item, amount));
        return true;
    }

    private static int FindSlot(ItemContainer container, Item item, int preferredSlot)
    {
        var used = container.Items.Select(existing => existing.Slot).ToHashSet();
        if (preferredSlot >= 0 && !used.Contains(preferredSlot) &&
            (container.ContainerSize < 0 || preferredSlot < container.ContainerSize))
            return preferredSlot;
        if (container is EquipmentContainer)
            return EquipmentContainer.GetAllowedGearSlots(item.Template).Select(slot => (int)slot)
                .FirstOrDefault(slot => !used.Contains(slot), -1);
        if (container.ContainerSize < 0)
            return used.Count == 0 ? 0 : checked(used.Max() + 1);
        for (var slot = 0; slot < container.ContainerSize; slot++)
            if (!used.Contains(slot))
                return slot;
        return -1;
    }

    private static bool IsHeldBy(Item item, ItemContainer container)
    {
        return item != null && item.Id != 0 && container != null && item.Count > 0 && item._holdingContainer == container &&
            item.OwnerId == container.OwnerId && item.SlotType == container.ContainerType &&
            container.Items.Count(existing => existing.Id == item.Id) == 1 && container.Items.Contains(item);
    }

    private void AddTask(ICharacter owner, ItemTask task)
    {
        if (owner == null)
            return;
        if (!_tasks.TryGetValue(owner, out var tasks))
            _tasks.Add(owner, tasks = []);
        // Tasks normally retain a live Item reference. Capture the wire value now so a
        // later grant/move in this mutation cannot alter an earlier task's count or slot.
        tasks.Add(new PreparedTask(task.Write(new PacketStream()).GetBytes()));
    }

    private void Capture(ItemContainer container) => _containers.TryAdd(container, new ContainerState(container));
    private void Capture(Item item) => _items.TryAdd(item, new ItemState(item));
    private bool Fail() { _failed = true; return false; }

    private void RequireActive()
    {
        RequireLock();
        ObjectDisposedException.ThrowIf(_finished, this);
    }

    private static void RequireLock()
    {
        if (!Monitor.IsEntered(SaveManager.PersistenceSyncRoot))
            throw new InvalidOperationException("Inventory mutations require the persistence lock.");
    }

    private sealed class PreparedTask(byte[] bytes) : ItemTask
    {
        public override PacketStream Write(PacketStream stream) => stream.Write(bytes);
    }

    private sealed class ContainerState(ItemContainer container)
    {
        private readonly List<Item> _items = [.. container.Items];
        private readonly bool _dirty = container.IsDirty;

        public void Restore()
        {
            container.Items = [.. _items];
            container.UpdateFreeSlotCount();
            container.IsDirty = _dirty;
        }
    }

    private sealed class ItemState(Item item)
    {
        private readonly ItemContainer _container = item._holdingContainer;
        private readonly ulong _owner = item.OwnerId;
        private readonly SlotType _type = item.SlotType;
        private readonly int _slot = item.Slot;
        private readonly int _count = item.Count;
        private readonly ItemFlag _flags = item.ItemFlags;
        private readonly byte _grade = item.Grade;
        private readonly bool _dirty = item.IsDirty;

        public void Restore()
        {
            item._holdingContainer = _container;
            item.OwnerId = _owner;
            item.SlotType = _type;
            item.Slot = _slot;
            item.Count = _count;
            item.ItemFlags = _flags;
            item.Grade = _grade;
            item.IsDirty = _dirty;
        }
    }
}
