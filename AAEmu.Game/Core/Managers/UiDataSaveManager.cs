using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>Coalesces UI changes without a write or a new timer for every client packet.</summary>
public sealed class UiDataSaveManager(IWorldManager worldManager) : Singleton<UiDataSaveManager>,
    IUiDataSaveManager, IInitializable, IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    internal const int BatchSize = 128;
    private readonly object _flushLock = new();
    private Timer _timer;
    private bool _stopped;
    private uint _lastCharacterId;
    internal Action<IReadOnlyList<Character>> WriteBatch { get; set; } = SaveBatch;

    public void Initialize()
    {
        lock (_flushLock)
        {
            if (!_stopped && _timer == null)
                _timer = new Timer(_ => FlushPending(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    internal void FlushPending()
    {
        if (!Monitor.TryEnter(_flushLock))
            return;
        try
        {
            if (_stopped)
                return;
            lock (SaveManager.PersistenceSyncRoot)
            {
                // Read the world under the same gate as deletion. No departed Character stays queued.
                var characters = worldManager.GetAllCharacters().Where(character => character.HasPendingUiData)
                    .OrderBy(character => character.Id <= _lastCharacterId).ThenBy(character => character.Id)
                    .Take(BatchSize).ToArray();
                if (characters.Length == 0)
                    return;
                _lastCharacterId = characters[^1].Id;
                WriteBatch(characters);
            }
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "Could not save UI data; the next tick or logout will retry");
        }
        finally { Monitor.Exit(_flushLock); }
    }

    internal static void SaveBatch(IReadOnlyList<Character> characters)
    {
        using var connection = MySQL.CreateConnection();
        foreach (var character in characters)
        {
            // The legacy options column is utf8mb3. One unsupported document must
            // not roll back or starve other characters in this bounded batch.
            try
            {
                using var transaction = connection.BeginTransaction();
                var context = new PersistenceSaveContext(connection, transaction);
                character.SaveUiOptions(connection, transaction, context, onlyDirty: true);
                transaction.Commit();
                context.AcknowledgeCommit();
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "Could not save pending UI data for character {CharacterId}", character.Id);
            }
        }
    }

    public void Dispose()
    {
        lock (_flushLock)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
        }
        // GameNetwork.Stop saves each active character before the final SaveManager checkpoint.
    }
}
