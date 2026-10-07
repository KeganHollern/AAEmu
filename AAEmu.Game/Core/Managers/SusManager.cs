using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using NLog;

namespace AAEmu.Game.Core.Managers;

public sealed class SusManager(IWorldManager worldManager) : Singleton<SusManager>, ISusManager, IInitializable, IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    public static string CategoryBot => "Bot";
    public static string CategoryBotReport => "BotReport";
    public static string CategoryCheating => "Cheat";
    public static string CategoryChatSpam => "ChatSpam";
    public static string CategoryRmt => "RMT";
    
    private Dictionary<uint, (Vector3 pos, float skipTime)> LastPlayerPositions { get; } = [];
    private Dictionary<uint, (Vector3 pos, float skipTime)> LastPetPositions { get; } = [];
    
    internal const int QueueCapacity = 1024;
    internal const int BatchSize = 128;
    internal const int MaximumDescriptionLength = 4096;
    private readonly object _queueLock = new();
    private readonly object _flushLock = new();
    private readonly Queue<Activity> _pending = [];
    private Timer _flushTimer;
    private bool _stopped;
    private long _dropped;
    internal Action<IReadOnlyList<Activity>> WriteBatch { get; set; } = InsertBatch;
    internal int PendingCount { get { lock (_queueLock) return _pending.Count; } }

    internal sealed record Activity(DateTime OccurredAt, string Category, uint AccountId,
        uint PlayerId, uint ZoneGroup, Vector3 Position, string Description);

    public void Initialize()
    {
        lock (_flushLock)
        {
            if (!_stopped && _flushTimer == null)
                _flushTimer = new Timer(_ => FlushPending(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    // Success means that the bounded queue accepted the record. It does not imply a database commit.
    public bool LogActivity(string category, uint accountId, uint playerId, uint zoneGroup, Vector3 position, string description)
    {
        lock (_queueLock)
        {
            if (_stopped || _pending.Count >= QueueCapacity ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            {
                ++_dropped;
                return false;
            }
            _pending.Enqueue(new Activity(DateTime.UtcNow, LimitText(category, 64), accountId, playerId,
                zoneGroup, position, LimitText(description, MaximumDescriptionLength)));
            return true;
        }
    }

    private static string LimitText(string value, int maximum)
    {
        if (value == null || value.Length <= maximum)
            return value ?? string.Empty;
        var length = char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum;
        return value[..length];
    }

    internal bool FlushPending(bool drain = false)
    {
        // Timer callbacks can overlap after a slow database response. Keep one writer.
        if (!Monitor.TryEnter(_flushLock))
            return false;
        try
        {
            if (_stopped && !drain)
                return false;
            Activity[] batch;
            long dropped;
            lock (_queueLock)
            {
                batch = _pending.Take(BatchSize).ToArray();
                dropped = _dropped;
                _dropped = 0;
            }
            if (dropped > 0)
                Logger.Warn("Suspicious activity queue rejected {Count} invalid or excess records", dropped);
            if (batch.Length == 0)
                return true;
            try
            {
                WriteBatch(batch);
                lock (_queueLock)
                {
                    for (var index = 0; index < batch.Length; ++index)
                        _pending.Dequeue();
                }
                Logger.Warn("Stored {Count} suspicious activity records", batch.Length);
                return true;
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Could not save the suspicious activity batch; the bounded queue retains it");
                return false;
            }
        }
        finally { Monitor.Exit(_flushLock); }
    }

    private static void InsertBatch(IReadOnlyList<Activity> batch)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        var values = new List<string>(batch.Count);
        for (var index = 0; index < batch.Count; ++index)
        {
            var activity = batch[index];
            values.Add($"(@date{index},@category{index},@account{index},@character{index},@zone{index},@x{index},@y{index},@z{index},@description{index})");
            command.Parameters.AddWithValue($"@date{index}", activity.OccurredAt);
            command.Parameters.AddWithValue($"@category{index}", activity.Category);
            command.Parameters.AddWithValue($"@account{index}", activity.AccountId);
            command.Parameters.AddWithValue($"@character{index}", activity.PlayerId);
            command.Parameters.AddWithValue($"@zone{index}", activity.ZoneGroup);
            command.Parameters.AddWithValue($"@x{index}", activity.Position.X);
            command.Parameters.AddWithValue($"@y{index}", activity.Position.Y);
            command.Parameters.AddWithValue($"@z{index}", activity.Position.Z);
            command.Parameters.AddWithValue($"@description{index}", activity.Description);
        }
        command.CommandText = "INSERT INTO audit_char_sus (sus_date, sus_category, sus_account, sus_character, zone_group, x, y, z, description) VALUES " + string.Join(',', values);
        if (command.ExecuteNonQuery() != batch.Count)
            throw new IOException("Suspicious activity batch did not insert every record.");
    }

    public void Dispose()
    {
        lock (_flushLock)
        {
            lock (_queueLock)
                _stopped = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
            while (PendingCount > 0)
            {
                if (!FlushPending(drain: true))
                    break;
            }
        }
    }

    public bool LogActivity(string category, Character player, string description)
    {
        return LogActivity(category, player?.AccountId ?? 0, player?.Id ?? 0, player?.Transform?.ZoneId ?? 0, player?.Transform?.World?.Position ?? Vector3.Zero, description);
    }
    
    public bool LogActivity(string category, string description)
    {
        return LogActivity(category, 0, 0, 0, Vector3.Zero, description);
    }

    #region movement_checks
    /// <summary>
    /// Do some analysis on player movement
    /// </summary>
    /// <param name="player"></param>
    /// <param name="deltaTime"></param>
    public void AnalyzePlayerDeltaMovement(Character player, float deltaTime)
    {
        if (!LastPlayerPositions.TryGetValue(player.Id, out var last))
        {
            LastPlayerPositions.Add(player.Id, (player.Transform.World.ClonePosition(), 0f));
            return;
        }
        
        var deltaPos = player.Transform.World.ClonePosition() - last.pos;
        var deltaFlatPos = deltaPos with { Z = 0 };

        if (player.IsRiding == false &&
            player.DisabledSetPosition == false &&
            deltaFlatPos != Vector3.Zero &&
            last.skipTime <= 0f)
        {
            var observedSpeed = deltaFlatPos.Length() / deltaTime;
            // var playerCheckSpeed = 5.0 * character.BaseMoveSpeed * character.MoveSpeedMul * 3.0;
            var playerCheckSpeed = 25.0;
            if (observedSpeed > playerCheckSpeed)
            {
                last.skipTime = 15f;
                // Looks like this player is moving a bit fast, better make a note of it
                LogActivity(CategoryCheating,
                    player,
                    $"Player {player.Name} seems to be moving a bit fast {observedSpeed:F1} m/s (max {playerCheckSpeed:F1})");
                // player.SendMessage($"Speed {observedSpeed:F1} m/s (max {playerCheckSpeed:F1}) !!!");
            }
            else
            {
                // player.SendMessage($"Speed {observedSpeed:F1} m/s (max {playerCheckSpeed:F1})");    
            }
        }

        last.skipTime -= deltaTime;
        LastPlayerPositions[player.Id] = (player.Transform.World.ClonePosition(), last.skipTime);
    }

    /// <summary>
    /// Resets current analysis of movement, call after things like teleport to prevent false positives
    /// </summary>
    /// <param name="playerId"></param>
    public void ResetAnalyzePlayerDeltaMovement(uint playerId)
    {
        _ = LastPlayerPositions.Remove(playerId);
    }

    /// <summary>
    /// Do some analysis on mount movement
    /// </summary>
    /// <param name="pet"></param>
    /// <param name="deltaTime"></param>
    public void AnalyzeMountDeltaMovement(Mate pet, float deltaTime)
    {
        if (!LastPetPositions.TryGetValue(pet.Id, out var last))
        {
            LastPetPositions.Add(pet.Id, (pet.Transform.World.ClonePosition(), 0f));
            return;
        }
        
        var deltaPos = pet.Transform.World.ClonePosition() - last.pos;
        var deltaFlatPos = deltaPos with { Z = 0 };

        if (pet.DisabledSetPosition == false &&
            deltaFlatPos != Vector3.Zero &&
            last.skipTime <= 0f)
        {
            var observedSpeed = deltaFlatPos.Length() / deltaTime;
            // var playerCheckSpeed = 5.0 * character.BaseMoveSpeed * character.MoveSpeedMul * 3.0;
            var playerCheckSpeed = 27.5;
            var petOwner = worldManager.GetCharacterByObjId(pet.OwnerObjId);
            if (observedSpeed > playerCheckSpeed)
            {
                last.skipTime = 15f;
                // Looks like this player is moving a bit fast, better make a note of it
                LogActivity(CategoryCheating,
                    petOwner,
                    $"Pet {pet.Name} from {petOwner?.Name} seems to be moving a bit fast {observedSpeed:F1} m/s (max {playerCheckSpeed:F1})");
                // petOwner?.SendMessage($"Pet Speed {observedSpeed:F1} m/s (max {playerCheckSpeed:F1}) !!!");
            }
            else
            {
                // petOwner?.SendMessage($"Pet Speed {observedSpeed:F1} m/s (max {playerCheckSpeed:F1})");    
            }
        }

        last.skipTime -= deltaTime;
        LastPetPositions[pet.Id] = (pet.Transform.World.ClonePosition(), last.skipTime);
    }
    
    /// <summary>
    /// Resets current analysis of movement, call after things like teleport to prevent false positives
    /// </summary>
    /// <param name="petId"></param>
    public void ResetAnalyzeMountDeltaMovement(uint petId)
    {
        _ = LastPetPositions.Remove(petId);
    }
    #endregion
}
