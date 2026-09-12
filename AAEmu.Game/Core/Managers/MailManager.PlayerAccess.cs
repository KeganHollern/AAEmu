using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Mails;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers;

public partial class MailManager
{
    internal Func<uint, uint, bool> OfflineReceiverBlocks { get; set; } = ReadOfflineReceiverBlock;

    internal MailResult CheckPlayerReceiver(uint receiverId, uint senderId)
    {
        var active = ActiveMailSender(receiverId);
        if (active == null)
            return MailResult.MailErrorOccurred;
        if (!active.Value)
            return MailResult.UnableToFindRecipient;

        try
        {
            // Online changes must take effect before the next character save.
            var receiver = worldManager.GetCharacterById(receiverId);
            var blocked = receiver != null
                ? CharacterBlocked.IsBlockedBy(receiver, senderId)
                : OfflineReceiverBlocks(receiverId, senderId);
            return blocked ? MailResult.CanNotBeMailed : MailResult.Success;
        }
        catch (MySqlException exception)
        {
            Logger.Warn(exception, "Cannot read recipient block state for player mail.");
            return MailResult.MailErrorOccurred;
        }
    }

    private static bool ReadOfflineReceiverBlock(uint receiverId, uint senderId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM blocked WHERE owner=@receiver AND blocked_id=@sender LIMIT 1";
        command.Parameters.AddWithValue("@receiver", receiverId);
        command.Parameters.AddWithValue("@sender", senderId);
        return command.ExecuteScalar() != null;
    }
}
