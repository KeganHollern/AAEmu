using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers;

public interface IAikaChatManager : IInitializable
{
    /// <summary>
    /// Observes a faction (ally) chat message and, when the configured trigger word is
    /// mentioned, schedules an in-character AI reply into the sender's faction channel.
    /// </summary>
    void OnFactionChatMessage(Character sender, string message);
}
