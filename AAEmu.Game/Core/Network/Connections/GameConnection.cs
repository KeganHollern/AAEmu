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
    private int _socketClosed;
    internal bool DisconnectSaveSucceeded { get; private set; } = true;
    private int _disconnected;
    internal object SessionSyncRoot { get; } = new();
    internal bool InGameCompleted { get; private set; }
    internal bool MatchesSession(ISession session) => ReferenceEquals(_session, session);

    public uint Id => _session.SessionId;
    public uint AccountId { get; set; }
    public bool IsAuthenticated { get; private set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    internal void MarkClosed() => Interlocked.Exchange(ref _closed, 1);
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
    internal ConnectionEventLimiter StateRejectionEvents { get; } = new();

    public GameConnection(ISession session)
    {
        _session = session;
        Subscribers = [];

        Characters = [];
        Houses = [];
        Payment = new AccountPayment();
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
            if (IsAuthenticated)
                return;
            Interlocked.Exchange(ref _closed, 1);
        }
        // Socket callbacks can wait for an active packet. Release the authentication
        // lock first so that packet can finish its authentication check.
        Shutdown();
    }

    internal bool TryAuthenticate(uint accountId)
    {
        lock (_authenticationLock)
        {
            if (accountId == 0 || IsAuthenticated || IsClosed)
                return false;
            AccountId = accountId;
            IsAuthenticated = true;
            State = GameState.Lobby;
            LastPing = DateTime.UtcNow;
            _authenticationTimer?.Dispose();
            _authenticationTimer = null;
            return true;
        }
    }

    internal bool TrySelectCharacter(Character character)
    {
        lock (SessionSyncRoot)
        {
            if (IsClosed || !IsAuthenticated || State != GameState.Lobby || ActiveChar != null ||
                character == null || character.AccountId != AccountId)
                return false;
            ActiveChar = character;
            State = GameState.CharacterSelected;
            InGameCompleted = false;
            return true;
        }
    }

    internal bool TryAdvanceWorldEntry(GameState expected, GameState next)
    {
        lock (SessionSyncRoot)
        {
            if (IsClosed || !IsAuthenticated || State != expected || ActiveChar?.AccountId != AccountId)
                return false;
            State = next;
            return true;
        }
    }

    internal bool TryCompleteWorldEntry()
    {
        lock (SessionSyncRoot)
        {
            if (IsClosed || !IsAuthenticated || State != GameState.World ||
                ActiveChar?.AccountId != AccountId || InGameCompleted)
                return false;
            InGameCompleted = true;
            return true;
        }
    }

    internal void ReturnToLobby()
    {
        ActiveChar = null;
        State = GameState.Lobby;
        InGameCompleted = false;
    }

    /// <summary>
    /// On Disconnect event
    /// </summary>
    public void OnDisconnect()
    {
        CompleteDisconnect(() =>
        {
            RunDisconnectStep(() => CancelTokenSource?.Cancel());
            LeaveTask = null;
            if (ActiveChar != null)
            {
                RunDisconnectStep(() => ChatManager.Instance.LeaveAllChannels(ActiveChar));
                RunDisconnectStep(() => AreaTriggerManager.Instance.EvictUnit(ActiveChar));
                RunDisconnectStep(() => TradeManager.Instance.CancelTrade(ActiveChar, 0));
                RunDisconnectStep(() => ActiveChar.IsOnline = false);
                foreach (var subscriber in ActiveChar.Subscribers.ToArray())
                    RunDisconnectStep(subscriber.Dispose);
                RunDisconnectStep(() => ActiveChar.Events?.OnDisconnect(this, new OnDisconnectArgs { Player = ActiveChar }));
                RunDisconnectStep(() => ActiveChar.RemoveAndDespawnActiveOwnedMatesSlaves());
                RunDisconnectStep(() => DoodadManager.Instance.CloseCoffersOpenedBy(ActiveChar));
            }
            foreach (var subscriber in Subscribers.ToArray())
                RunDisconnectStep(subscriber.Dispose);
        }, () => SaveAndRemoveFromWorld(ActiveChar), () =>
        {
            AccountManager.Instance.Remove(this);
            RunDisconnectStep(() => AccountManager.Instance.UpdateLoginTime(AccountId, DateTime.UtcNow));
        });
    }

    internal bool CompleteDisconnect(Action cleanup, Action save, Action remove)
    {
        lock (SessionSyncRoot)
        {
            _authenticationTimer?.Dispose();
            Interlocked.Exchange(ref _closed, 1);
            if (Interlocked.Exchange(ref _disconnected, 1) != 0 || !IsAuthenticated || AccountId == 0)
                return DisconnectSaveSucceeded;
            try
            {
                RunDisconnectStep(cleanup);
                try { save(); }
                catch (Exception exception)
                {
                    DisconnectSaveSucceeded = false;
                    Logger.Error(exception, "Could not save departing session {ConnectionId}", Id);
                }
            }
            finally
            {
                try { RunDisconnectStep(remove); }
                finally { ActiveChar = null; }
            }
            return DisconnectSaveSucceeded;
        }
    }

    internal static void RunDisconnectStep(Action action)
    {
        try { action(); }
        catch (Exception exception) { Logger.Error(exception, "Disconnect cleanup step failed"); }
    }

    internal bool KickDuplicate()
    {
        lock (SessionSyncRoot)
        {
            Interlocked.Exchange(ref _closed, 1);
            try
            {
                RunDisconnectStep(() => SendPacket(new SCKickedPacket(KickedReason.KickDuplicateAccount, string.Empty)));
                OnDisconnect();
                return DisconnectSaveSucceeded;
            }
            finally { Shutdown(); }
        }
    }

    /// <summary>
    /// Closes the active connection session
    /// </summary>
    public void Shutdown()
    {
        _authenticationTimer?.Dispose();
        Interlocked.Exchange(ref _closed, 1);
        if (Interlocked.Exchange(ref _socketClosed, 1) == 0)
            _session?.Close();
    }

    public void Kick(string reason)
    {
        Interlocked.Exchange(ref _closed, 1);
        // A command can target another session while its packet owns that session's
        // lock. Queue a busy target to avoid two moderation commands waiting on each other.
        if (!Monitor.TryEnter(SessionSyncRoot))
        {
            _ = Task.Run(() => KickWhenIdle(reason));
            return;
        }
        try { KickWhenIdle(reason); }
        finally { Monitor.Exit(SessionSyncRoot); }
    }

    private void KickWhenIdle(string reason)
    {
        lock (SessionSyncRoot)
        {
            RunDisconnectStep(() => DisconnectWithSave(
                () => SendPacket(new SCKickedPacket(KickedReason.KickByGm, reason)),
                OnDisconnect, Shutdown));
        }
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
        if (!IsAuthenticated || AccountId == 0 || IsClosed || State != GameState.Lobby || ActiveChar != null)
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

        RunDisconnectStep(() => activeChar.ParentWorld?.SphereQuestManager?.RemoveSphereQuestTriggers(activeChar));
        RunDisconnectStep(() => TradeManager.Instance.CancelTrade(activeChar, 0));
        RunDisconnectStep(() => RadarManager.Instance.UnRegister(activeChar));
        RunDisconnectStep(() => activeChar.Buffs?.CancelAllEffectTasks());
        RunDisconnectStep(activeChar.Delete);
        RunDisconnectStep(() =>
        {
            if (WorldManager.Instance.GetCharacterByObjId(activeChar.ObjId) == activeChar)
                WorldManager.Instance.TryRemoveCharacter(activeChar.ObjId);
        });
        if (!activeChar.SaveDirectlyToDatabase())
            throw new IOException($"Could not save departing character {activeChar.Id}.");
    }
}
