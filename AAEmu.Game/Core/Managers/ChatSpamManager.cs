using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;

using Microsoft.Extensions.Options;

namespace AAEmu.Game.Core.Managers;

public class ChatSpamManager : Singleton<ChatSpamManager>, IChatSpamManager
{
    private const int CleanupInterval = 256;

    private readonly object _lock = new();
    private readonly ISusManager _susManager;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<AppConfiguration> _options;
    private readonly ChatSpamGameData _gameData;
    private readonly Dictionary<uint, AccountChatState> _accountStates = [];
    private int _checksSinceCleanup;

    public ChatSpamManager(
        ISusManager susManager,
        TimeProvider timeProvider,
        IOptions<AppConfiguration> options)
        : this(susManager, timeProvider, options, ChatSpamGameData.Instance)
    {
    }

    internal ChatSpamManager(
        ISusManager susManager,
        TimeProvider timeProvider,
        IOptions<AppConfiguration> options,
        ChatSpamGameData gameData)
    {
        _susManager = susManager;
        _timeProvider = timeProvider;
        _options = options;
        _gameData = gameData;
    }

    public ChatSpamCheckResult CheckMessage(Character character, ChatType chatType, string message)
    {
        ArgumentNullException.ThrowIfNull(character);

        var config = _options.Value.ChatSpam;
        if (config is not { Enabled: true })
            return ChatSpamCheckResult.Allowed;

        message ??= string.Empty;
        var now = _timeProvider.GetUtcNow();
        ChatSpamCheckResult result;
        string reviewDescription = null;

        lock (_lock)
        {
            if (++_checksSinceCleanup >= CleanupInterval)
            {
                CleanUpExpiredStates(now, config);
                _checksSinceCleanup = 0;
            }

            if (!_accountStates.TryGetValue(character.AccountId, out var state))
            {
                state = new AccountChatState();
                _accountStates.Add(character.AccountId, state);
            }

            state.LastActivity = now;
            if (state.MutedUntil > now)
            {
                return new ChatSpamCheckResult(
                    ChatSpamViolationType.Muted,
                    ErrorMessageType.YellTooOften,
                    state.MutedUntil);
            }

            PruneMessages(state, now, config);

            if (_gameData.TryMatch(message, out var match))
            {
                reviewDescription =
                    $"Chat spam rule {match.RuleId} ({match.RuleName}) detail {match.DetailId} " +
                    $"matched {character.Name} in {chatType}: \"{message}\"";
                result = ApplyViolation(
                    state,
                    now,
                    config,
                    ChatSpamViolationType.Rule,
                    ErrorMessageType.SaidForbiddenWord,
                    match.RuleId,
                    match.DetailId);
            }
            else if (ReachedRateLimit(state, now, config))
            {
                reviewDescription =
                    $"Chat rate limit reached by {character.Name} in {chatType}: " +
                    $"{config.RateMessageCount} messages in {config.RateWindowSeconds:g} seconds; \"{message}\"";
                result = ApplyViolation(
                    state,
                    now,
                    config,
                    ChatSpamViolationType.RateLimit,
                    ErrorMessageType.YellTooOften);
            }
            else
            {
                var normalizedMessage = NormalizeMessage(message);
                if (ReachedRepeatLimit(state, now, normalizedMessage, config))
                {
                    reviewDescription =
                        $"Repeated chat detected from {character.Name} in {chatType}: " +
                        $"{config.RepeatMessageCount} repeated messages in {config.RepeatWindowSeconds:g} seconds; \"{message}\"";
                    result = ApplyViolation(
                        state,
                        now,
                        config,
                        ChatSpamViolationType.RepeatedMessage,
                        ErrorMessageType.YellTooOften);
                }
                else
                {
                    state.Messages.Add(new ChatMessageRecord(now, normalizedMessage));
                    result = ChatSpamCheckResult.Allowed;
                }
            }
        }

        if (reviewDescription != null)
        {
            var category = result.Violation == ChatSpamViolationType.Rule
                ? SusManager.CategoryRmt
                : SusManager.CategoryChatSpam;
            _susManager.LogActivity(category, character, reviewDescription);
        }

        return result;
    }

    private static ChatSpamCheckResult ApplyViolation(
        AccountChatState state,
        DateTimeOffset now,
        ChatSpamConfig config,
        ChatSpamViolationType violation,
        ErrorMessageType errorMessage,
        uint ruleId = 0,
        uint detailId = 0)
    {
        var muteDuration = GetDuration(config.MuteSeconds);
        state.MutedUntil = AddDuration(now, muteDuration);
        state.Messages.Clear();
        return new ChatSpamCheckResult(violation, errorMessage, state.MutedUntil, ruleId, detailId);
    }

    private static bool ReachedRateLimit(AccountChatState state, DateTimeOffset now, ChatSpamConfig config)
    {
        var window = GetDuration(config.RateWindowSeconds);
        if (config.RateMessageCount == 0 || window == TimeSpan.Zero)
            return false;

        var cutoff = SubtractDuration(now, window);
        var recentMessageCount = state.Messages.Count(record => record.Timestamp > cutoff);
        return recentMessageCount + 1 >= config.RateMessageCount;
    }

    private static bool ReachedRepeatLimit(
        AccountChatState state,
        DateTimeOffset now,
        string normalizedMessage,
        ChatSpamConfig config)
    {
        var window = GetDuration(config.RepeatWindowSeconds);
        if (config.RepeatMessageCount == 0 || window == TimeSpan.Zero ||
            normalizedMessage.Length < config.MinimumRepeatLength)
        {
            return false;
        }
        if (config.RepeatMessageCount == 1)
            return true;

        var cutoff = SubtractDuration(now, window);
        var repeatCount = 1;

        foreach (var record in state.Messages)
        {
            if (record.Timestamp <= cutoff ||
                !string.Equals(record.NormalizedMessage, normalizedMessage, StringComparison.Ordinal))
            {
                continue;
            }

            repeatCount++;
            if (repeatCount >= config.RepeatMessageCount)
                return true;
        }

        return false;
    }

    private void CleanUpExpiredStates(DateTimeOffset now, ChatSpamConfig config)
    {
        var retention = new[]
        {
            GetDuration(config.RateWindowSeconds),
            GetDuration(config.RepeatWindowSeconds),
            GetDuration(config.MuteSeconds)
        }.Max();
        var cutoff = SubtractDuration(now, retention);

        foreach (var accountId in _accountStates
                     .Where(pair => pair.Value.MutedUntil <= now && pair.Value.LastActivity <= cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _accountStates.Remove(accountId);
        }
    }

    private static void PruneMessages(AccountChatState state, DateTimeOffset now, ChatSpamConfig config)
    {
        var retention = new[]
        {
            GetDuration(config.RateWindowSeconds),
            GetDuration(config.RepeatWindowSeconds)
        }.Max();
        var cutoff = SubtractDuration(now, retention);
        state.Messages.RemoveAll(record => record.Timestamp <= cutoff || record.Timestamp > now);
    }

    private static string NormalizeMessage(string message)
    {
        return message.Trim().ToUpperInvariant();
    }

    private static TimeSpan GetDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0d)
            return TimeSpan.Zero;

        var ticks = seconds * TimeSpan.TicksPerSecond;
        return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    private static DateTimeOffset AddDuration(DateTimeOffset value, TimeSpan duration)
    {
        var remaining = DateTimeOffset.MaxValue - value;
        return duration >= remaining ? DateTimeOffset.MaxValue : value + duration;
    }

    private static DateTimeOffset SubtractDuration(DateTimeOffset value, TimeSpan duration)
    {
        var elapsed = value - DateTimeOffset.MinValue;
        return duration >= elapsed ? DateTimeOffset.MinValue : value - duration;
    }

    private sealed class AccountChatState
    {
        public DateTimeOffset LastActivity { get; set; }
        public DateTimeOffset MutedUntil { get; set; }
        public List<ChatMessageRecord> Messages { get; } = [];
    }

    private sealed record ChatMessageRecord(DateTimeOffset Timestamp, string NormalizedMessage);
}
