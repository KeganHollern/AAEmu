using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Music;
using AAEmu.Game.Models.Game.Skills;
using NLog;

namespace AAEmu.Game.Core.Managers;

public class MusicManager(IMusicIdManager musicIdManager, IItemManager itemManager) : Singleton<MusicManager>, IMusicManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, SongData> _uploadQueue = []; // playerId, song
    private Dictionary<uint, SongData> _allSongs = []; // songId, song
    private Dictionary<uint, byte[]> _midiCache = []; // playerId, midi data

    public void Load()
    {
        _uploadQueue = [];
        _allSongs = [];
        _midiCache = [];

        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM music";
                command.Prepare();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var songData = new SongData
                        {
                            Id = reader.GetUInt32("id"),
                            AuthorId = reader.GetUInt32("author"),
                            Title = reader.GetString("title"),
                            Song = reader.GetString("song")
                        };
                        _allSongs.Add(songData.Id, songData);
                    }
                }
            }
        }
    }

    public bool Save(SongData songData)
    {
        songData.Id = musicIdManager.GetNextId();

        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "REPLACE INTO music (" +
                                      "`id`,`author`,`title`,`song` ) VALUES ( " +
                                      "@id, @author, @title, @song" +
                                      " )";
                command.Parameters.AddWithValue("@id", songData.Id);
                command.Parameters.AddWithValue("@author", songData.AuthorId);
                command.Parameters.AddWithValue("@title", songData.Title);
                command.Parameters.AddWithValue("@song", songData.Song);
                command.Prepare();
                if (command.ExecuteNonQuery() != 1)
                {
                    Logger.Warn("Error saving song to DB for {0} ({1})", songData.Title, songData.Id);
                    return false;
                }
            }
        }
        _allSongs.Add(songData.Id, songData);

        return true;
    }

    public void UploadSong(uint charId, string title, string song, ulong itemId)
    {
        lock (SaveManager.PersistenceSyncRoot)
            _uploadQueue[charId] = new SongData
            {
                AuthorId = charId, Title = title, Song = song, SourceItemId = itemId
            };
    }

    public bool CreateSheetMusic(Character player, Item sourceItem)
    {
        var batch = SkillLaborBatch.For(player);
        if (batch == null)
            return false;
        if (sourceItem == null || !ReferenceEquals(player.Inventory.GetItemById(sourceItem.Id), sourceItem) ||
            !ReferenceEquals(sourceItem._holdingContainer, player.Inventory.Bag) ||
            TradeReservation.GetReservedCount(sourceItem) != 0 ||
            !_uploadQueue.TryGetValue(player.Id, out var upload) || upload.SourceItemId != sourceItem.Id ||
            upload.Title == null || upload.Title.Length > 128 || upload.Song == null)
            return batch.Fail();

        // Consume first: one last sheet of paper can free the output slot in a full bag.
        if (!batch.Inventory.TryConsume(player.Inventory.Bag, sourceItem, 1))
            return batch.Fail();
        var song = new SongData
        {
            Id = musicIdManager.GetNextId(), AuthorId = player.Id,
            Title = upload.Title, Song = upload.Song, SourceItemId = sourceItem.Id
        };
        batch.Enlist(null, () => musicIdManager.ReleaseId(song.Id));
        var created = itemManager.Create(Item.SheetMusic, 1, 0, true);
        if (created is not MusicSheetItem sheet)
        {
            if (created != null)
                itemManager.ReleaseId(created.Id);
            return batch.Fail();
        }
        sheet.OwnerId = player.Id;
        sheet.MadeUnitId = player.Id;
        sheet.SongId = song.Id;
        if (!batch.Inventory.TryAddCreated(sheet, player.Inventory.Bag))
        {
            player.SendErrorMessage(ErrorMessageType.BagFull);
            return batch.Fail();
        }
        batch.Enlist(context =>
        {
            using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = "INSERT INTO music (`id`,`author`,`title`,`song`) VALUES (@id,@author,@title,@song)";
            command.Parameters.AddWithValue("@id", song.Id);
            command.Parameters.AddWithValue("@author", song.AuthorId);
            command.Parameters.AddWithValue("@title", song.Title);
            command.Parameters.AddWithValue("@song", song.Song);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Could not save the prepared sheet music.");
            context.AfterCommit(() =>
            {
                _allSongs.Add(song.Id, song);
                if (_uploadQueue.TryGetValue(player.Id, out var current) && ReferenceEquals(current, upload))
                    _uploadQueue.Remove(player.Id);
            });
        }, null);
        return true;
    }

    public SongData GetSongById(uint songId)
    {
        lock (SaveManager.PersistenceSyncRoot)
            return _allSongs.GetValueOrDefault(songId);
    }

    public void CacheMidi(uint playerId, byte[] midiData)
    {
        _midiCache.Remove(playerId);
        _midiCache.Add(playerId, midiData);
    }

    public byte[] GetMidiCache(uint playerId)
    {
        if (_midiCache.TryGetValue(playerId, out var data))
            return data;
        return [];
    }
}
