using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Core.Managers.UnitManagers;

public partial class CharacterManager
{
    internal bool TrySelectCharacter(GameConnection connection, Character character)
    {
        // Packets already hold the session gate. Keep that lock order for direct
        // callers too; deletion never waits for a session while it owns persistence.
        lock (connection.SessionSyncRoot)
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (character.IsDeleted)
                return false;
            using var database = MySQL.CreateConnection();
            return ReadDeletionTimes(character, database) && character.DeleteTime == DateTime.MinValue &&
                connection.TrySelectCharacter(character);
        }
    }

    private bool IsCharacterInPlay(Character character, GameConnection connection) =>
        character.IsOnline || connection?.ActiveChar?.Id == character.Id ||
        character.Connection?.ActiveChar?.Id == character.Id || worldManager.GetCharacterById(character.Id) != null ||
        GameConnectionTable.Instance.GetConnections().Any(candidate => candidate.ActiveChar?.Id == character.Id) ||
        (worldManager.GetWorlds()?.Any(world => world.GetAllSlaves()
            .Any(slave => slave.Summoner?.Id == character.Id)) ?? false);

    internal static ErrorMessageType GetDeletionRestriction(Character character, IEnumerable<uint> activeBuffIds)
    {
        var buffs = activeBuffIds.ToHashSet();
        if (buffs.Overlaps([(uint)BuffConstants.SuspectedUser, (uint)BuffConstants.PrimeSuspect,
                (uint)BuffConstants.TransformingIntoPrimeSuspect, (uint)BuffConstants.Prisoner_Bot]))
            return ErrorMessageType.CannotDeleteCharWhileBotSuspected;
        if (character.HasPendingTrial || buffs.Overlaps([(uint)BuffConstants.Prisoner_Nuian,
                (uint)BuffConstants.Prisoner_Haranyan, (uint)BuffConstants.ForciblyAwaitingTrial,
                (uint)BuffConstants.Trial_Defendant]))
            return ErrorMessageType.CannotDeleteCharWhilePenalty;
        return ErrorMessageType.NoErrorMessage;
    }

    private static ErrorMessageType ReadDeletionRestriction(Character character, MySqlConnection connection)
    {
        // Character selection does not load active buffs. Read saved rows without
        // applying their gameplay callbacks to an offline character.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT buff_id FROM character_active_buffs WHERE character_id=@id AND " +
            "((duration=0 AND buff_id=@suspected) OR (time_left>0 AND " +
            "(real_time=0 OR TIMESTAMPDIFF(MICROSECOND,saved_at,UTC_TIMESTAMP(6)) < CAST(time_left AS SIGNED)*1000)))";
        command.Parameters.AddWithValue("@id", character.Id);
        command.Parameters.AddWithValue("@suspected", (uint)BuffConstants.SuspectedUser);
        var buffs = new List<uint>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                buffs.Add(reader.GetUInt32(0));
        return GetDeletionRestriction(character, buffs);
    }

    public void SetDeleteCharacter(GameConnection gameConnection, uint characterId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!gameConnection.Characters.TryGetValue(characterId, out var character) || character.IsDeleted)
            {
                gameConnection.SendPacket(new SCDeleteCharacterResponsePacket(characterId, 0));
                return;
            }
            if (character.Expedition?.OwnerId == character.Id)
            {
                gameConnection.SendPacket(new SCErrorMsgPacket(ErrorMessageType.ExpeditionOwnerCannotDelete, 0, true));
                return;
            }
            if (gameConnection.ActiveChar != null || IsCharacterInPlay(character, gameConnection))
            {
                gameConnection.SendPacket(new SCErrorMsgPacket(ErrorMessageType.CannotDeleteCharWhileOnPlay, 0, true));
                return;
            }
            if (!gameConnection.IsAuthenticated || gameConnection.IsClosed || character.AccountId != gameConnection.AccountId)
                return;
            using var connection = MySQL.CreateConnection();
            if (!ReadDeletionTimes(character, connection))
            {
                gameConnection.SendPacket(new SCDeleteCharacterResponsePacket(characterId, 0));
                return;
            }
            var error = ReadDeletionRestriction(character, connection);
            if (error != ErrorMessageType.NoErrorMessage)
            {
                gameConnection.SendPacket(new SCErrorMsgPacket(error, 0, true));
                return;
            }
            if (character.DeleteTime == DateTime.MinValue)
            {
                var requested = DateTime.UtcNow;
                var delay = 0;
                foreach (var timing in AppConfiguration.Instance.Account.DeleteTimings)
                    if (character.Level >= timing.Level)
                        delay = timing.Delay;
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE characters SET delete_request_time=@requested,delete_time=@due " +
                    "WHERE id=@id AND account_id=@account AND deleted=0 AND delete_time=@minimum";
                command.Parameters.AddWithValue("@requested", requested);
                command.Parameters.AddWithValue("@due", requested.AddMinutes(delay));
                command.Parameters.AddWithValue("@id", character.Id);
                command.Parameters.AddWithValue("@account", character.AccountId);
                command.Parameters.AddWithValue("@minimum", DateTime.MinValue);
                if (command.ExecuteNonQuery() != 1)
                {
                    gameConnection.SendPacket(new SCDeleteCharacterResponsePacket(characterId, 0));
                    return;
                }
                character.DeleteRequestTime = requested;
                character.DeleteTime = requested.AddMinutes(delay);
            }
            gameConnection.SendPacket(new SCDeleteCharacterResponsePacket(character.Id, 2, character.DeleteRequestTime, character.DeleteTime));
        }
        CheckForDeletedCharacters();
    }

    public void SetRestoreCharacter(GameConnection gameConnection, uint characterId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!gameConnection.IsAuthenticated || gameConnection.IsClosed ||
                !gameConnection.Characters.TryGetValue(characterId, out var character) ||
                character.AccountId != gameConnection.AccountId || character.IsDeleted)
            {
                gameConnection.SendPacket(new SCCancelCharacterDeleteResponsePacket(characterId, 4));
                return;
            }
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE characters SET delete_request_time=@minimum,delete_time=@minimum " +
                "WHERE id=@id AND account_id=@account AND deleted=0";
            command.Parameters.AddWithValue("@minimum", DateTime.MinValue);
            command.Parameters.AddWithValue("@id", character.Id);
            command.Parameters.AddWithValue("@account", character.AccountId);
            if (command.ExecuteNonQuery() != 1)
            {
                gameConnection.SendPacket(new SCCancelCharacterDeleteResponsePacket(characterId, 4));
                return;
            }
            character.DeleteRequestTime = DateTime.MinValue;
            character.DeleteTime = DateTime.MinValue;
            gameConnection.SendPacket(new SCCancelCharacterDeleteResponsePacket(character.Id, 3));
        }
    }

    private static bool ReadDeletionTimes(Character character, MySqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT delete_request_time,delete_time,offline_guilty_time FROM characters " +
            "WHERE id=@id AND account_id=@account AND deleted=0";
        command.Parameters.AddWithValue("@id", character.Id);
        command.Parameters.AddWithValue("@account", character.AccountId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        character.DeleteRequestTime = reader.GetDateTime(0);
        character.DeleteTime = reader.GetDateTime(1);
        character.OfflineGuiltyTime = reader.GetInt32(2);
        return true;
    }

    private bool CompleteCharacterDeletion(Character character, GameConnection gameConnection, MySqlConnection dbConnection)
    {
        if (character.IsDeleted || character.Expedition?.OwnerId == character.Id ||
            IsCharacterInPlay(character, gameConnection) || !ReadDeletionTimes(character, dbConnection) ||
            character.DeleteTime == DateTime.MinValue || character.DeleteTime > DateTime.UtcNow ||
            ReadDeletionRestriction(character, dbConnection) != ErrorMessageType.NoErrorMessage ||
            CharacterDeletionStore.HasAuctionObligations(dbConnection, character.Id))
            return false;

        // The approved rule keeps deletion pending until owned listings and
        // current winning bids finish through the ordinary auction scheduler.
        // Returning player mail is already durable and idempotent. Keep deletion
        // pending when an attachment or destination prevents a complete return.
        if (!mailManager.ReturnDeletedCharacterMail(character.Id))
            return false;
        var received = mailManager.AllPlayerMails.Values.Where(mail => mail.Header.ReceiverId == character.Id).ToArray();
        if (received.Any(mail => mail.HasUnresolvedAttachments || mail.Body.Attachments.Any(item =>
                item == null || item.OwnerId != character.Id || !ReferenceEquals(itemManager.GetItemByItemId(item.Id), item))))
            return false;
        using var mails = mailManager.BeginMutation();
        foreach (var mail in received)
            if (!mails.TryRemove(mail))
                return false;

        var deletedName = AppConfiguration.Instance.Account.DeleteReleaseName ? "!" + character.Name : character.Name;
        bool committed;
        try
        {
            committed = SaveManager.Instance.TryCommitEconomy([], context =>
            {
                CharacterDeletionStore.Delete(context, character, deletedName);
                context.AfterCommit(() =>
                {
                    character.IsDeleted = true;
                    itemManager.ForgetDeletedCharacterAssets(character.Id);
                });
            });
        }
        catch
        {
            mails.PreservePreparedState();
            throw;
        }
        if (!committed)
            return false;
        // Neither notification failures nor a stale Character instance may undo SQL.
        mails.Complete(false);
        nameManager.MarkCharacterDeleted(character.Id, deletedName, AppConfiguration.Instance.Account.DeleteReleaseName);
        DeleteCharacterWorldAssets(character, false);
        gameConnection?.SendPacket(new SCCharacterDeletedPacket(character.Id, character.Name));
        gameConnection?.SendPacket(new SCDeleteCharacterResponsePacket(character.Id, 1, character.DeleteRequestTime, character.DeleteTime));
        Logger.Info("Deleted Account:{0} Id:{1} Name:{2}", character.AccountId, character.Id, character.Name);
        return true;
    }
}
