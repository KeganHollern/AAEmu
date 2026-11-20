using System.Numerics;
using System.Runtime.CompilerServices;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils.DB;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Services.Security;
using NLog;

namespace AAEmu.Game.Core.Managers;

public sealed class SusManager(IWorldManager worldManager, GameSecurityMetrics securityMetrics = null) : Singleton<SusManager>, ISusManager, IInitializable, IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    public static string CategoryBot => "Bot";
    public static string CategoryBotReport => "BotReport";
    public static string CategoryCheating => "Cheat";
    public static string CategoryChatSpam => "ChatSpam";
    public static string CategoryRmt => "RMT";

    // A late callback from an old login or summon only has access to its own sample.
    private static readonly Lock s_movementLock = new();
    private static readonly ConditionalWeakTable<Unit, MovementSample> s_movementSamples = new();

    private sealed class MovementSample(WorldInstance world, Vector3 position)
    {
        public WorldInstance World { get; } = world;
        public Vector3 Position { get; set; } = position;
        public float SkipTime { get; set; }
    }

    internal const int QueueCapacity = 1024;
    internal const int BatchSize = 128;
    internal const int MaximumDescriptionLength = 4096;
    private readonly object _queueLock = new();
    private readonly object _flushLock = new();
    private readonly Queue<Activity> _pending = [];
    private Timer _flushTimer;
    private bool _stopped;
    private long _dropped;
    // Keep event detail bounded until this backlog drains; counters include every report.
    private ConnectionEventLimiter _activityEvents = new();
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
        string outcome;
        bool emitEvent;
        lock (_queueLock)
        {
            outcome = _stopped ? "stopped" : _pending.Count >= QueueCapacity ? "queue_full" :
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ? "invalid" : "queued";
            if (outcome == "queued")
                _pending.Enqueue(new Activity(DateTime.UtcNow, LimitText(category, 64), accountId, playerId,
                    zoneGroup, position, LimitText(description, MaximumDescriptionLength)));
            else
                ++_dropped;
            emitEvent = _activityEvents.TryConsume();
        }
        GameSecurityTelemetry.SuspiciousActivity(category, outcome, emitEvent, securityMetrics);
        return outcome == "queued";
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
                    if (_pending.Count == 0)
                        _activityEvents = new ConnectionEventLimiter();
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

    public void AnalyzePlayerDeltaMovement(Character player, float deltaTime)
    {
        const float checkSpeed = 25f;
        if (ObserveMovement(player, deltaTime, checkSpeed) is { } speed)
            LogActivity(CategoryCheating, player,
                $"Player {player.Name} seems to be moving a bit fast {speed:F1} m/s (max {checkSpeed:F1})");
    }

    public void AnalyzeMountDeltaMovement(Mate pet, float deltaTime)
    {
        const float checkSpeed = 27.5f;
        if (ObserveMovement(pet, deltaTime, checkSpeed) is not { } speed)
            return;

        var owner = worldManager.GetCharacterByObjId(pet.OwnerObjId);
        LogActivity(CategoryCheating, owner,
            $"Pet {pet.Name} from {owner?.Name} seems to be moving a bit fast {speed:F1} m/s (max {checkSpeed:F1})");
    }

    private static float? ObserveMovement(Unit unit, float deltaTime, float checkSpeed)
    {
        lock (s_movementLock)
        {
            var world = unit.ParentWorld;
            if (unit.DisabledSetPosition || !ReferenceEquals(world?.GetUnit(unit.ObjId), unit) ||
                !float.IsFinite(deltaTime) || deltaTime <= 0f)
                return null;

            var position = unit.Transform.World.ClonePosition();
            if (!s_movementSamples.TryGetValue(unit, out var last) || !ReferenceEquals(last.World, world))
            {
                s_movementSamples.AddOrUpdate(unit, new MovementSample(world, position));
                return null;
            }

            float? reportedSpeed = null;
            if (unit is not Character { IsRiding: true } && last.SkipTime <= 0f)
            {
                var delta = (position - last.Position) with { Z = 0f };
                var speed = delta.Length() / deltaTime;
                if (speed > checkSpeed)
                {
                    reportedSpeed = speed;
                    last.SkipTime = 15f;
                }
            }

            last.Position = position;
            last.SkipTime -= deltaTime;
            return reportedSpeed;
        }
    }

    internal static void ResetMovementAnalysis(Unit unit)
    {
        lock (s_movementLock)
            s_movementSamples.Remove(unit);
    }
}
