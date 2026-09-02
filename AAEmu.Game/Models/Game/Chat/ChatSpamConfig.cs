namespace AAEmu.Game.Models.Game.Chat;

/// <summary>
/// Server enforcement settings that complement the message-content graph in compact data.
/// The compact chat-spam tables do not contain timing, repeat, or mute parameters.
/// </summary>
public class ChatSpamConfig
{
    /// <summary>
    /// Enables compact-data content rules, rate limiting, repeat detection, and temporary mutes.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Number of messages in <see cref="RateWindowSeconds"/> that triggers a violation.
    /// Set either value to zero to disable rate limiting.
    /// </summary>
    public uint RateMessageCount { get; set; } = 10;

    public double RateWindowSeconds { get; set; } = 5d;

    /// <summary>
    /// Number of repeated messages in <see cref="RepeatWindowSeconds"/> that triggers a violation.
    /// Leading/trailing whitespace and letter casing are ignored.
    /// Set either value to zero to disable repeat detection.
    /// </summary>
    public uint RepeatMessageCount { get; set; } = 5;

    public double RepeatWindowSeconds { get; set; } = 60d;

    /// <summary>
    /// Minimum message length eligible for repeat detection.
    /// </summary>
    public uint MinimumRepeatLength { get; set; } = 10;

    /// <summary>
    /// Duration applied after any violation. Set to zero to reject only the violating message.
    /// </summary>
    public double MuteSeconds { get; set; } = 600d;
}
