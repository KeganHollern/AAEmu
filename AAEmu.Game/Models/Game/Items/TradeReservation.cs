using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Models.Game.Items;

/// <summary>Reserves offered quantities without moving assets out of their owner's inventory.</summary>
public sealed class TradeReservation : IDisposable
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly Dictionary<Item, (TradeReservation Reservation, int Count)> s_items = [];
    private static readonly Dictionary<Character, (TradeReservation Reservation, int Money)> s_money = [];
    private bool _disposed;
    private readonly Action _onInvalidated;

    public TradeReservation(Action onInvalidated = null)
    {
        _onInvalidated = onInvalidated;
    }

    public bool TryReserve(Item item, int count)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (item == null || count <= 0 || count > item.Count || s_items.ContainsKey(item))
                return false;
            s_items.Add(item, (this, count));
            return true;
        }
    }

    public bool TryReserve(Character character, int money)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (character == null || money < 0 || money > character.Money ||
                (s_money.TryGetValue(character, out var existing) && existing.Reservation != this))
                return false;
            if (money == 0)
                s_money.Remove(character);
            else
                s_money[character] = (this, money);
            return true;
        }
    }

    public void Release(Item item)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (item != null && s_items.TryGetValue(item, out var existing) && existing.Reservation == this)
                s_items.Remove(item);
        }
    }

    public static int GetReservedCount(Item item)
    {
        lock (SaveManager.PersistenceSyncRoot)
            return item != null && s_items.TryGetValue(item, out var entry) ? entry.Count : 0;
    }

    public static int GetReservedMoney(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
            return character != null && s_money.TryGetValue(character, out var entry) ? entry.Money : 0;
    }

    public static bool HasReservations(Character character)
    {
        lock (SaveManager.PersistenceSyncRoot)
            return character != null && (s_money.ContainsKey(character) ||
                s_items.Keys.Any(item => item.OwnerId == character.Id));
    }

    public static void Invalidate(Item item)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (item == null || !s_items.TryGetValue(item, out var entry))
                return;
            entry.Reservation.Dispose();
            try
            {
                entry.Reservation._onInvalidated?.Invoke();
            }
            catch (Exception exception)
            {
                // The trade callback retires its state before sending cancellation.
                // A notification failure must not prevent the item's expiry removal.
                s_logger.Error(exception, "Unable to notify invalidated trade reservation for item {0}.", item.Id);
            }
        }
    }

    public void Dispose()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var item in s_items.Where(pair => pair.Value.Reservation == this).Select(pair => pair.Key).ToArray())
                s_items.Remove(item);
            foreach (var character in s_money.Where(pair => pair.Value.Reservation == this).Select(pair => pair.Key).ToArray())
                s_money.Remove(character);
        }
    }
}
