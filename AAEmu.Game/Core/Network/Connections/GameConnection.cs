using System.Net;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;

using NLog;

namespace AAEmu.Game.Core.Network.Connections;

public class GameConnection
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ISession _session;
    private readonly Lock _authenticationLock = new();
    private Timer _authenticationTimer;
    private int _closed;
    private int _disconnected;

    public uint Id => _session.SessionId;
    public uint AccountId { get; set; }
    public bool IsAuthenticated { get; private set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public IPAddress Ip => _session.Ip;
    public PacketStream LastPacket { get; set; }
    public AccountPayment Payment { get; set; }
    public int PacketCount { get; set; }
    private List<IDisposable> Subscribers { get; set; }
    public GameState State { get; set; }
    public Character ActiveChar { get; set; }
    public Dictionary<uint, Character> Characters { get; set; }
    public Dictionary<uint, House> Houses { get; set; }
    public Task LeaveTask { get; set; }
    public CancellationTokenSource CancelTokenSource { get; set; }
    public DateTime LastPing { get; set; }
    internal ConnectionEventLimiter UnknownPacketEvents { get; } = new();

    public GameConnection(ISession session)
    {
        _session = session;
        Subscribers = [];

        Characters = [];
        Houses = [];
        Payment = new AccountPayment(this);
        // AddAttribute("gmFlag", true);
    }

    /// <summary>
    /// Encodes and sends a single game packet to the active connection
    /// </summary>
    /// <param name="packet"></param>
    public void SendPacket(GamePacket packet)
    {
        if (packet.TypeId == 0xFFF)
        {
            Logger.Error("Dropping invalid game packet with opcode 0xFFF.");
            return;
        }

        packet.Connection = this;
        SendPacket(packet.Encode());
    }

    /// <summary>
    /// Sends RAW packet data to the active connection
    /// </summary>
    /// <param name="packet"></param>
    private void SendPacket(byte[] packet)
    {
        _session?.SendPacket(packet);
    }

    /// <summary>
    /// On connect event
    /// </summary>
    public void OnConnect()
    {
        StartAuthenticationTimeout(TimeSpan.FromSeconds(10));
    }

    internal void StartAuthenticationTimeout(TimeSpan timeout)
    {
        lock (_authenticationLock)
        {
            if (IsAuthenticated || IsClosed)
                return;
            _authenticationTimer?.Dispose();
            _authenticationTimer = new Timer(_ => CloseIfUnauthenticated(), null, timeout, Timeout.InfiniteTimeSpan);
        }
    }

    internal void CloseIfUnauthenticated()
    {
        lock (_authenticationLock)
        {
            if (!IsAuthenticated)
                Shutdown();
        }
    }

    internal bool TryAuthenticate(uint accountId)
    {
        lock (_authenticationLock)
        {
            if (accountId == 0 || IsAuthenticated || IsClosed)
                return false;
            AccountId = accountId;
            IsAuthenticated = true;
            _authenticationTimer?.Dispose();
            _authenticationTimer = null;
            return true;
        }
    }

    /// <summary>
    /// On Disconnect event
    /// </summary>
    public void OnDisconnect()
    {
        _authenticationTimer?.Dispose();
        if (Interlocked.Exchange(ref _disconnected, 1) != 0 || !IsAuthenticated || AccountId == 0)
            return;

        try
        {
            CleanupThenSave(() =>
            {
                AccountManager.Instance.Remove(AccountId);
                if (ActiveChar != null)
                {
                    ChatManager.Instance.LeaveAllChannels(ActiveChar);
                    AreaTriggerManager.Instance.EvictUnit(ActiveChar);
                    TradeManager.Instance.CancelTrade(ActiveChar, 0);
                    // The hard-disconnect path must also publish offline team/friend state.
                    if (ActiveChar.IsOnline)
                        ActiveChar.IsOnline = false;
                    foreach (var subscriber in ActiveChar.Subscribers)
                        subscriber.Dispose();
                    ActiveChar.Events?.OnDisconnect(this, new OnDisconnectArgs { Player = ActiveChar });
                    ActiveChar.RemoveAndDespawnActiveOwnedMatesSlaves();
                    DoodadManager.Instance.CloseCoffersOpenedBy(ActiveChar);
                }
                foreach (var subscriber in Subscribers)
                    subscriber.Dispose();
            }, () =>
            {
                SaveAndRemoveFromWorld(ActiveChar);
                AccountManager.Instance.UpdateLoginTime(AccountId, DateTime.UtcNow);
            });
        }
        finally
        {
            ActiveChar = null;
        }
    }

    /// <summary>
    /// Closes the active connection session
    /// </summary>
    public void Shutdown()
    {
        _authenticationTimer?.Dispose();
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _session?.Close();
    }

    public void Kick(string reason)
    {
        DisconnectWithSave(
            () => SendPacket(new SCKickedPacket(KickedReason.KickByGm, reason)),
            OnDisconnect, Shutdown);
    }

    internal static void DisconnectWithSave(Action notify, Action save, Action close)
    {
        try
        {
            try { notify(); }
            finally { save(); }
        }
        finally { close(); }
    }

    internal static void CleanupThenSave(Action cleanup, Action save)
    {
        try { cleanup(); }
        finally { save(); }
    }

    /// <summary>
    /// Adds a named attribute object to the connection 
    /// </summary>
    /// <param name="name"></param>
    /// <param name="value"></param>
    public void AddAttribute(string name, object value)
    {
        _session.AddAttribute(name, value);
    }

    /// <summary>
    /// Gets a named attribute of the connection
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public object GetAttribute(string name)
    {
        return _session.GetAttribute(name);
    }

    /// <summary>
    /// Adds a subscriber object
    /// </summary>
    /// <param name="disposable"></param>
    public void PushSubscriber(IDisposable disposable)
    {
        Subscribers.Add(disposable);
    }

    /// <summary>
    /// Loads Account data for the connection like characters and houses
    /// </summary>
    public void LoadAccount()
    {
        if (!IsAuthenticated || AccountId == 0)
        {
            Shutdown();
            return;
        }
        // TODO: Load payment and account tier information

        // Load character info for this account
        Characters.Clear();
        using (var connection = MySQL.CreateConnection())
        {
            var characterIds = new List<uint>();
            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.CommandText = "SELECT id FROM characters WHERE `account_id` = @account_id and `deleted`=0";
                command.Parameters.AddWithValue("@account_id", AccountId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        characterIds.Add(reader.GetUInt32("id"));
                }
            }

            foreach (var id in characterIds)
            {
                var character = Character.Load(connection, id, AccountId);
                if (character == null)
                    continue; // TODO ...
                if (!CharacterManager.Instance.CheckForDeletedCharactersDeletion(character, this, connection))
                {
                    Characters.Add(character.Id, character);
                }
            }

            /*
            foreach (var character in Characters.Values)
                character.Inventory.Load(connection, SlotType.Equipment);
            */
        }

        // Load housing info for this account
        Houses.Clear();
        HousingManager.Instance.GetByAccountId(Houses, AccountId);
    }

    /// <summary>
    /// Called when closing a connection
    /// </summary>
    public static void SaveAndRemoveFromWorld(Character activeChar)
    {
        // TODO: this needs a rewrite
        if (activeChar == null)
            return;

        CleanupThenSave(() =>
        {
            TradeManager.Instance.CancelTrade(activeChar, 0);

            // Remove Radars
            RadarManager.Instance.UnRegister(activeChar);

            // Cancel all running buff effect tasks before removing the character.
            // The buffs themselves are saved to DB inside SaveDirectlyToDatabase() → Character.Save().
            activeChar.Buffs?.CancelAllEffectTasks();

            // Hide/Despawn the player
            activeChar.Delete();
            // Removed ReleaseId here to try and fix party/raid disconnect and reconnect issues. Replaced with saving the data
            //ObjectIdManager.Instance.ReleaseId(ActiveChar.ObjId);

            // Also drop the entry from WorldManager._characters. Without this, hard-DC
            // / crash paths leak a ghost reference at that ObjId (LeaveWorldTask does
            // the same TryRemoveCharacter explicitly on graceful logout — we have to
            // mirror it here or the next reconnect will TryAddCharacter on a stale slot
            // and end up with a divergent _characters[id] = OLD vs _baseUnits[id] = NEW,
            // so any later operation on the ghost reference Deletes the live character.
            //
            // Guard with an identity check so the cleanup stays safe if ObjId recycling
            // is ever re-enabled: only remove the slot if _characters still maps this
            // ObjId to OUR character — never evict a freshly-spawned entity that
            // happened to inherit the recycled ObjId.
            if (WorldManager.Instance.GetCharacterByObjId(activeChar.ObjId) == activeChar)
                WorldManager.Instance.TryRemoveCharacter(activeChar.ObjId);

        }, () =>
        {
            // A cleanup failure must not skip the departing character's save attempt.
            if (!activeChar.SaveDirectlyToDatabase())
                throw new IOException($"Could not save departing character {activeChar.Id}.");
        });
    }
}
