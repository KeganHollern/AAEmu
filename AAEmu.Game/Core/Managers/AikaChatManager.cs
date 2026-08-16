using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.StaticValues;
using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Forwards faction chat that mentions the configured trigger word to an
/// OpenAI-compatible chat-completions endpoint and posts the reply back into the
/// same faction channel as a named speaker. One request is in flight per faction;
/// a mention that arrives while its faction is busy is served once the current
/// reply lands, using the newest history.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class AikaChatManager : Singleton<AikaChatManager>, IAikaChatManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    internal readonly record struct ChatLine(string Speaker, string Text, bool IsBot);

    private sealed class ChannelState
    {
        public readonly object Sync = new();
        public readonly Queue<ChatLine> History = new();
        public bool Busy;
        public bool Pending;
    }

    private readonly ConcurrentDictionary<FactionsEnum, ChannelState> _channels = new();
    private HttpClient _httpClient;
    private Regex _trigger;

    private static AiChatConfig Config => AppConfiguration.Instance.AiChat ?? new AiChatConfig();

    public void Initialize()
    {
        var config = Config;
        if (!config.Enabled)
        {
            Logger.Info("AI faction chat is disabled");
            return;
        }

        _trigger = BuildTriggerRegex(config.TriggerWord);
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 300))
        };
        Logger.Info($"AI faction chat enabled: \"{config.BotName}\" answers to \"{config.TriggerWord}\" via {config.Endpoint} (model {config.Model})");
    }

    public void OnFactionChatMessage(Character sender, string message)
    {
        var config = Config;
        if (!config.Enabled || _httpClient == null || _trigger == null)
            return;
        if (sender?.Faction == null || string.IsNullOrWhiteSpace(message))
            return;
        // The bot must never talk to itself, no matter what a player names a character.
        if (string.Equals(sender.Name, config.BotName, StringComparison.OrdinalIgnoreCase))
            return;

        var channel = ChatManager.Instance.GetFactionChat(sender.Faction.MotherId);
        if (channel.ChatType != ChatType.Ally)
            return; // NullChannel fallback for factions without an ally channel.

        var state = _channels.GetOrAdd(channel.Faction, _ => new ChannelState());
        bool startReply;
        lock (state.Sync)
        {
            AppendHistoryLocked(state, new ChatLine(sender.Name, message, false), config);
            startReply = _trigger.IsMatch(message);
            if (startReply)
            {
                if (state.Busy)
                {
                    state.Pending = true;
                    startReply = false;
                }
                else
                {
                    state.Busy = true;
                }
            }
        }

        if (startReply)
            _ = ReplyAsync(channel, state, config);
    }

    private async Task ReplyAsync(ChatChannel channel, ChannelState state, AiChatConfig config)
    {
        try
        {
            ChatLine[] history;
            lock (state.Sync)
                history = [.. state.History];

            var payload = BuildRequestPayload(config, channel.InternalName, history);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(config.Endpoint, content);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            var reply = SanitizeReply(ExtractContent(body), config.BotName, config.MaxReplyLength);
            if (reply.Length == 0)
                reply = "H-hmph! I wasn't even listening!";

            channel.SendPacket(new SCChatMessagePacket(ChatType.Ally, channel.Faction, config.BotName, reply));
            lock (state.Sync)
                AppendHistoryLocked(state, new ChatLine(config.BotName, reply, true), config);
            Logger.Info($"{config.BotName} replied in {channel.InternalName} faction chat ({reply.Length} chars)");
        }
        catch (Exception e)
        {
            Logger.Warn(e, $"AI faction chat reply failed for {channel.InternalName}");
        }
        finally
        {
            bool rerun;
            lock (state.Sync)
            {
                rerun = state.Pending;
                state.Pending = false;
                state.Busy = rerun;
            }
            if (rerun)
                _ = ReplyAsync(channel, state, config);
        }
    }

    private static void AppendHistoryLocked(ChannelState state, ChatLine line, AiChatConfig config)
    {
        var text = line.Text.Length > config.MaxContextLineLength
            ? line.Text[..config.MaxContextLineLength]
            : line.Text;
        state.History.Enqueue(line with { Text = text });
        while (state.History.Count > Math.Max(1, config.HistoryLength))
            state.History.Dequeue();
    }

    /// <summary>Word-boundary, case-insensitive matcher for the trigger word.</summary>
    internal static Regex BuildTriggerRegex(string word)
    {
        return new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    internal static string BuildSystemPrompt(AiChatConfig config, string channelName)
    {
        if (!string.IsNullOrWhiteSpace(config.SystemPrompt))
            return config.SystemPrompt;

        return
            $"You are {config.BotName}, a tsundere anime girl hanging out in the {channelName} faction chat of an ArcheAge server. " +
            "Personality: sharp-tongued, teasing, easily flustered, secretly caring; you deny caring about the players, yet you always show up. " +
            "Chat lines are shown as \"Name: text\". " +
            $"Rules: reply with exactly one short chat line under {Math.Max(40, config.MaxReplyLength - 60)} characters, plain text only. " +
            "No markdown, no surrounding quotes, and never prefix your own name. " +
            "Match the language the players are writing in. " +
            "Stay in character; never mention AI, models, or prompts.";
    }

    /// <summary>Builds the chat-completions request body from the remembered channel history.</summary>
    internal static string BuildRequestPayload(AiChatConfig config, string channelName, IReadOnlyList<ChatLine> history)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt(config, channelName) }
        };
        foreach (var line in history)
        {
            messages.Add(line.IsBot
                ? new JsonObject { ["role"] = "assistant", ["content"] = line.Text }
                : new JsonObject { ["role"] = "user", ["content"] = $"{line.Speaker}: {line.Text}" });
        }

        var root = new JsonObject
        {
            ["model"] = config.Model,
            ["temperature"] = config.Temperature,
            ["top_p"] = config.TopP,
            ["top_k"] = 20,
            ["min_p"] = 0.0,
            ["max_tokens"] = config.MaxTokens,
            // This companion is for quick banter: reasoning stays off (see issue #49).
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["messages"] = messages
        };
        return root.ToJsonString();
    }

    /// <summary>Pulls choices[0].message.content out of a chat-completions response.</summary>
    internal static string ExtractContent(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var messageContent)
                && messageContent.ValueKind == JsonValueKind.String)
            {
                return messageContent.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fall through: treated as an empty reply by the caller.
        }
        return string.Empty;
    }

    /// <summary>
    /// Turns raw model output into a single safe chat line: drops any reasoning block,
    /// flattens whitespace, strips wrapping quotes and a leading bot-name prefix, and
    /// word-truncates to the configured cap.
    /// </summary>
    internal static string SanitizeReply(string raw, string botName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = Regex.Replace(raw, "<think>.*?</think>", string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (text.Length >= 2
            && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\u201C' && text[^1] == '\u201D')))
        {
            text = text[1..^1].Trim();
        }

        if (text.StartsWith(botName + ":", StringComparison.OrdinalIgnoreCase))
            text = text[(botName.Length + 1)..].Trim();

        maxLength = Math.Max(8, maxLength);
        if (text.Length > maxLength)
        {
            var cut = text.LastIndexOf(' ', maxLength - 1);
            text = text[..(cut > maxLength / 2 ? cut : maxLength - 1)].TrimEnd() + "\u2026";
        }

        return text;
    }
}
