using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;

namespace AAEmu.Game.Core.Managers;

public interface IChatSpamManager
{
    ChatSpamCheckResult CheckMessage(Character character, ChatType chatType, string message);
}
