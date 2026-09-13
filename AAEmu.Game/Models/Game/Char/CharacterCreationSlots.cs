using System.Security.Cryptography;
using System.Text;

using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.StaticValues;

using NLog;

namespace AAEmu.Game.Models.Game.Char;

internal static class CharacterCreationSlots
{
    public const byte DefaultCharacterSlots = 6;
    // r208022 adds SCGetSlotCount to Login's default limit. It is not the used slot count.
    public const byte ExpandedCharacterSlots = 0;
    public const int MaximumCharacters = DefaultCharacterSlots + ExpandedCharacterSlots;

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    internal static CharacterCreateError Create(uint accountId, Func<CharacterCreateError> create)
    {
        if (accountId == 0)
            return CharacterCreateError.Failed;

        using var connection = MySQL.CreateConnection();
        // A database-scoped advisory lock also serializes requests from separate Game processes.
        // Keep it until the character and starter items finish their normal persistence commit.
        var databaseKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.Database)))[..24];
        var lockName = $"aaemu:character:{databaseKey}:{accountId}";
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name, 10)";
        command.Parameters.AddWithValue("@name", lockName);
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            return CharacterCreateError.ServerError;

        try
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id=@account AND deleted=0";
            count.Parameters.AddWithValue("@account", accountId);
            if (Convert.ToInt64(count.ExecuteScalar()) >= MaximumCharacters)
                return CharacterCreateError.WorldCharacterLimit;

            return create();
        }
        finally
        {
            command.CommandText = "SELECT RELEASE_LOCK(@name)";
            try
            {
                command.ExecuteScalar();
            }
            catch (Exception exception)
            {
                // Closing a failed physical connection releases its advisory lock.
                MySql.Data.MySqlClient.MySqlConnection.ClearPool(connection);
                Logger.Error(exception, "Failed to release character creation lock");
            }
        }
    }
}
