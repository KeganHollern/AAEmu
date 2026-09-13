using System.Net;
using System.Collections.Concurrent;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Login;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Packets.G2L;
using AAEmu.Game.Core.Packets.Proxy;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.StaticValues;

using NLog;

namespace AAEmu.Game.Core.Managers.World;

public class EnterWorldManager(
    IAccountManager accountManager,
    IStreamManager streamManager,
    IQuestManager questManager,
    IChatManager chatManager,
    IFamilyManager familyManager,
    IWorldManager worldManager,
    IModerationManager moderationManager = null) : Singleton<EnterWorldManager>, IEnterWorldManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly PendingWorldAdmissions _pending = new();
    private readonly ConcurrentDictionary<uint, long> _admissionRequests = new();
    private long _nextAdmission;
    private readonly object[] _admissionLocks = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();

    public void AddAccount(uint accountId, uint connectionId, uint token, ulong patronStart, ulong patronEnd, IPAddress address)
    {
        if (!AccountPayment.ValidPeriod(patronStart, patronEnd))
            return;
        long request;
        lock (_admissionLocks[accountId % (uint)_admissionLocks.Length])
        {
            request = Interlocked.Increment(ref _nextAdmission);
            _admissionRequests[accountId] = request;
        }
        _ = AddAccountAsync(accountId, connectionId, token, new AccountPayment(patronStart, patronEnd), address, request);
    }

    private async Task AddAccountAsync(uint accountId, uint connectionId, uint token, AccountPayment payment, IPAddress address, long request)
    {
        var loginConnection = LoginNetwork.Instance.GetConnection();
        var gsId = AppConfiguration.Instance.Id;
        try
        {
            if (loginConnection == null)
                return;
            var moderation = moderationManager ?? ModerationManager.Instance;
            if (accountId == 0 || !await moderation.RefreshAccountAsync(accountId))
            {
                loginConnection.SendPacket(new GLPlayerEnterPacket(connectionId, gsId, 1));
                return;
            }

            lock (_admissionLocks[accountId % (uint)_admissionLocks.Length])
            {
                var admitted = _admissionRequests.GetValueOrDefault(accountId) == request &&
                    PreparePendingAccount(accountId, token, payment, address, moderation);
                loginConnection.SendPacket(new GLPlayerEnterPacket(connectionId, gsId, admitted ? (byte)0 : (byte)1));
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Could not authorize pending account {AccountId}", accountId);
            loginConnection?.SendPacket(new GLPlayerEnterPacket(connectionId, gsId, 1));
        }
        finally
        {
            ((ICollection<KeyValuePair<uint, long>>)_admissionRequests).Remove(new(accountId, request));
        }
    }

    internal bool PreparePendingAccount(uint accountId, uint token, AccountPayment payment, IPAddress address,
        IModerationManager moderation)
    {
        lock (_admissionLocks[accountId % (uint)_admissionLocks.Length])
        {
            var stored = false;
            if (!moderation.TryAdmit(accountId, () => stored = _pending.TryAdd(token, accountId, payment, address)) || !stored)
                return false;
            // The admission lock prevents cookie consumption until the old save completes.
            try
            {
                var previous = accountManager.GetConnection(accountId);
                if (previous == null || previous.KickDuplicate())
                    return true;
                _pending.Remove(token);
                return false;
            }
            catch
            {
                _pending.Remove(token);
                throw;
            }
        }
    }

    internal bool SetPendingAccount(uint token, uint accountId, AccountPayment payment = null) =>
        _pending.TryAdd(token, accountId, payment, IPAddress.Loopback);

    internal void RemovePendingAccount(uint token) => _pending.Remove(token);

    internal PendingWorldAccountResult ConsumePendingAccount(uint token, uint accountId) => ConsumePendingAccount(token, accountId, out _);

    internal PendingWorldAccountResult ConsumePendingAccount(uint token, uint accountId, out AccountPayment payment) =>
        _pending.Consume(token, accountId, IPAddress.Loopback, true, out payment);

    public void Login(GameConnection connection, uint accountId, uint token)
    {
        if (accountId == 0 || connection.AccountId != 0 || connection.IsAuthenticated || connection.IsClosed ||
            connection.State != GameState.Connected || connection.ActiveChar != null)
        {
            connection.Shutdown();
            return;
        }
        lock (_admissionLocks[accountId % (uint)_admissionLocks.Length])
        {
            var result = _pending.Consume(token, accountId, connection.Ip,
                AppConfiguration.Instance.Network.ValidateWorldCookieAddress, out var payment);
            if (result != PendingWorldAccountResult.Consumed)
            {
                Logger.Warn("Rejected world cookie ({Result}) from {RemoteIp}", result, connection.Ip);
                connection.Shutdown();
                return;
            }

            var streamToken = 0u;
            var moderation = moderationManager ?? ModerationManager.Instance;
            var admitted = moderation.TryAdmit(accountId, () =>
            {
                if (accountManager.Contains(accountId) || !connection.TryAuthenticate(accountId))
                    return;
                connection.Payment = payment;
                accountManager.Add(connection);
                if (accountManager.IsCurrent(connection) && !connection.IsClosed)
                    streamToken = streamManager.AddToken(connection);
            });
            if (!admitted || !connection.IsAuthenticated || connection.IsClosed || streamToken == 0)
            {
                connection.Shutdown();
                return;
            }

            var port = AppConfiguration.Instance.StreamNetwork.Port;
            var gm = connection.GetAttribute("gmFlag") != null;
            connection.SendPacket(new X2EnterWorldResponsePacket(0, gm, streamToken, port));
            connection.SendPacket(new ChangeStatePacket(0));
        }
    }

    /// <summary>
    /// Start a leave world task
    /// Delay is also calculated
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="leaveWorldTargetType"></param>
    public void Leave(GameConnection connection, LeaveWorldTargetType leaveWorldTargetType)
    {
        switch (leaveWorldTargetType)
        {
            case LeaveWorldTargetType.QuitGame: // выход из игры, quit game
            case LeaveWorldTargetType.CharacterSelect: // выход к списку персонажей, go to character select
                if (connection.State == GameState.World)
                {

                    if (connection.LeaveTask != null)
                    {
                        break;
                    }

                    // Say goodbye if player is quitting (but not going to character select)
                    if (leaveWorldTargetType == 0)
                        connection.ActiveChar?.SendMessage(ChatType.System, AppConfiguration.Instance.World.LogoutMessage);

                    var logoutTime = 10000; // in ms

                    // Make it 5 minutes if you're still in combat
                    if (connection.ActiveChar?.IsInBattle ?? false)
                        logoutTime *= 30;

                    // Add 10 minutes if you have a Slave Active
                    if (connection.ActiveChar?.ParentWorld?.SlaveManager.GetActiveSlaveByOwnerObjId(connection.ActiveChar.ObjId) != null)
                        logoutTime += 1000 * 60 * 10;

                    connection.SendPacket(new SCPrepareLeaveWorldPacket(logoutTime, leaveWorldTargetType, false));

                    connection.CancelTokenSource = new CancellationTokenSource();
                    var token = connection.CancelTokenSource.Token;
                    var leavingCharacter = connection.ActiveChar;
                    connection.LeaveTask = Task.Run(async () =>
                    {
                        await Task.Delay(logoutTime, token);
                        lock (connection.SessionSyncRoot)
                        {
                            if (!token.IsCancellationRequested)
                                LeaveWorldTask(connection, leaveWorldTargetType, leavingCharacter);
                        }
                    }, token);
                }

                break;
            case LeaveWorldTargetType.ServerSelect: // выбор сервера, server select
                if (connection.State == GameState.Lobby)
                {
                    var gsId = AppConfiguration.Instance.Id;
                    LoginNetwork
                        .Instance
                        .GetConnection()
                        .SendPacket(new GLPlayerReconnectPacket(gsId, connection.AccountId, ReconnectTokenManager.Instance.Issue(connection)));
                }

                break;
            default:
                Logger.Warn($"[Leave] Unknown type: {leaveWorldTargetType}");
                break;
        }
    }

    /// <summary>
    /// Actually leave the game world and return connection to lobby state
    /// Also despawns all owned mounts/pet/vehicles still in the world
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="leaveWorldTarget"></param>
    /// <param name="activeChar"></param>
    public void LeaveWorldTask(GameConnection connection, LeaveWorldTargetType leaveWorldTarget, Character activeChar)
    {
        if (connection == null)
        {
            LeaveWorldCore(null, leaveWorldTarget, activeChar);
            return;
        }
        lock (connection.SessionSyncRoot)
        {
            if (connection.IsClosed || connection.State != GameState.World || !ReferenceEquals(connection.ActiveChar, activeChar))
                return;
            LeaveWorldCore(connection, leaveWorldTarget, activeChar);
        }
    }

    private void LeaveWorldCore(GameConnection connection, LeaveWorldTargetType leaveWorldTarget, Character activeChar)
    {
        if (activeChar != null)
        {
            GameConnection.RunDisconnectStep(() => TradeManager.Instance.CancelTrade(activeChar, 0));
            activeChar.DisabledSetPosition = true;
            GameConnection.RunDisconnectStep(() => activeChar.IsOnline = false);
            activeChar.LeaveTime = DateTime.UtcNow;
            GameConnection.RunDisconnectStep(() => questManager.RemoveQuestTimer(activeChar.Id, 0));
            GameConnection.RunDisconnectStep(() => activeChar.ParentWorld.MateManager.RemoveAndDespawnAllActiveOwnedMates(activeChar));
            GameConnection.RunDisconnectStep(() => activeChar.ParentWorld.SlaveManager.RemoveAndDespawnAllActiveOwnedSlaves(activeChar));
            GameConnection.RunDisconnectStep(() => DoodadManager.Instance.CloseCoffersOpenedBy(activeChar));
            GameConnection.RunDisconnectStep(() => activeChar.ForceDismount());
            GameConnection.RunDisconnectStep(() => chatManager.LeaveAllChannels(activeChar));
            if (activeChar.Family > 0)
                GameConnection.RunDisconnectStep(() => familyManager.OnCharacterLogout(activeChar));
            GameConnection.RunDisconnectStep(() => activeChar.Expedition?.OnCharacterLogout(activeChar));
            GameConnection.RunDisconnectStep(() => activeChar.BuyBackItems.Wipe());
            foreach (var subscriber in activeChar.Subscribers.ToArray())
                GameConnection.RunDisconnectStep(subscriber.Dispose);
        }

        try { GameConnection.SaveAndRemoveFromWorld(activeChar); }
        catch (Exception exception)
        {
            Logger.Error(exception, "Could not save character {CharacterId} on return to lobby", activeChar?.Id);
            connection?.Shutdown();
            return;
        }
        if (connection != null)
        {
            connection.ReturnToLobby();
            connection.LeaveTask = null;
            connection.SendPacket(new SCLeaveWorldGrantedPacket(leaveWorldTarget));
            connection.SendPacket(new ChangeStatePacket(0));
        }
    }
}
