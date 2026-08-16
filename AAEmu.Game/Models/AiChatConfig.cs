namespace AAEmu.Game.Models;

/// <summary>
/// Settings for the AI faction-chat companion. Disabled by default; when enabled,
/// the game server forwards faction chat that mentions <see cref="TriggerWord"/> to an
/// OpenAI-compatible chat-completions endpoint and posts the reply back into the
/// same faction channel as <see cref="BotName"/>.
/// </summary>
public class AiChatConfig
{
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible chat-completions URL (e.g. llama.cpp server).</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:8080/v1/chat/completions";

    public string Model { get; set; } = "qwen";

    /// <summary>Display name used for replies in chat.</summary>
    public string BotName { get; set; } = "Aika";

    /// <summary>Word that summons the bot. Matched case-insensitively on word boundaries.</summary>
    public string TriggerWord { get; set; } = "aika";

    /// <summary>Recent faction-chat lines (per faction) sent as conversation context.</summary>
    public int HistoryLength { get; set; } = 24;

    /// <summary>Longest chat line each remembered message contributes to the prompt.</summary>
    public int MaxContextLineLength { get; set; } = 200;

    public int MaxTokens { get; set; } = 160;

    public double Temperature { get; set; } = 1.0;

    public double TopP { get; set; } = 0.95;

    public int RequestTimeoutSeconds { get; set; } = 45;

    /// <summary>Hard cap for the reply posted to chat; longer output is word-truncated.</summary>
    public int MaxReplyLength { get; set; } = 240;

    /// <summary>Optional replacement for the built-in personality prompt.</summary>
    public string SystemPrompt { get; set; } = string.Empty;
}
