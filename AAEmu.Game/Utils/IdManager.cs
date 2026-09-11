using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;

using NLog;

namespace AAEmu.Game.Utils;

public class IdManager
{
    // ReSharper disable once MemberCanBePrivate.Global
    protected static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private BitSet _freeIds;
    private int _freeIdCount;
    private int _nextFreeId;
    private bool _initialized;

    private readonly string _name;
    private readonly uint _firstId; // = 0x00000001;
    private readonly uint _lastId; // = 0xFFFFFFFF;
    private readonly uint[] _exclude;
    private readonly int _freeIdSize;
    private readonly string[,] _objTables;
    private readonly bool _distinct;
    private readonly string[,] _retainedObjTables;
    private readonly HashSet<uint> _retainedIds = [];
    private readonly object _lock = new();

    // ReSharper disable once MemberCanBeProtected.Global
    public IdManager(
        string name,
        uint firstId,
        uint lastId,
        string[,] objTables,
        uint[] exclude,
        bool distinct = false,
        string[,] retainedObjTables = null)
    {
        _name = name;
        _firstId = firstId;
        _lastId = lastId;
        _objTables = objTables;
        _exclude = exclude;
        _distinct = distinct;
        _retainedObjTables = retainedObjTables;
        // BitSet uses signed indexes, even when the configured ID range uses uint.
        _freeIdSize = (int)Math.Min((long)_lastId - _firstId, int.MaxValue);
        if (_freeIdSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(lastId));
        PrimeFinder.Init();
    }

    /// <summary>Called by the ManagerOrchestrator in Stage 2, delegating to Initialize().</summary>
    public void Load() => Initialize();

    /// <summary>
    /// Initializes the IdManager for use by resetting the Ids and grabbing data from the database if needed
    /// </summary>
    /// <param name="forceReset">When true forces the re-initialization even if it was previously initialized already</param>
    /// <returns></returns>
    public bool Initialize(bool forceReset = false)
    {
        if (_initialized && forceReset == false)
            return true;

        try
        {
            _freeIds = new BitSet(Math.Min(PrimeFinder.NextPrime(100000), _freeIdSize));
            _freeIds.Clear();
            _freeIdCount = _freeIdSize;

            var allUsedObjects = Array.Empty<uint>();
            var retainedObjectIds = Array.Empty<uint>();
            try
            {
                allUsedObjects = ExtractUsedObjectIdTable(_objTables, _distinct);
                if (_retainedObjTables?.Length >= 2)
                    retainedObjectIds = ExtractUsedObjectIdTable(_retainedObjTables, true);
            }
            catch
            {
                Logger.Warn($"{_name} failed to read from database, reverting to default");
            }

            foreach (var usedObjectId in allUsedObjects)
            {
                if (_exclude.Contains(usedObjectId))
                    continue;
                var offset = (long)usedObjectId - _firstId;
                if (offset < 0 || offset >= _freeIdSize)
                {
                    Logger.Warn($"{_name}: Object ID {usedObjectId} in DB is outside the allocator bounds");
                    continue;
                }
                var objectId = (int)offset;

                if (objectId >= _freeIds.Count)
                    IncreaseBitSetCapacity(objectId + 1);
                _freeIds.Set(objectId);
                Interlocked.Decrement(ref _freeIdCount);
            }

            _retainedIds.Clear();
            _retainedIds.UnionWith(retainedObjectIds);

            _nextFreeId = _freeIds.NextClear(0);
            Logger.Info($"{_name} successfully initialized");
        }
        catch (Exception e)
        {
            Logger.Error($"{_name} could not be initialized correctly");
            Logger.Error(e);
            return false;
        }

        _initialized = true;
        return true;
    }

    private uint[] ExtractUsedObjectIdTable(string[,] objTables, bool distinct)
    {
        if (objTables.Length < 2)
            return [];

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        var persistedIdsQuery = "SELECT " + (distinct ? "DISTINCT " : "") +
                                objTables[0, 1] + " AS object_id FROM " +
                                objTables[0, 0] + " WHERE " + objTables[0, 1] + " IS NOT NULL";
        for (var i = 1; i < objTables.Length / 2; i++)
            persistedIdsQuery += (distinct ? " UNION SELECT " : " UNION ALL SELECT ") +
                                 objTables[i, 1] + " AS object_id FROM " + objTables[i, 0] +
                                 " WHERE " + objTables[i, 1] + " IS NOT NULL";
        var query = "SELECT object_id, 0 AS i FROM (" + persistedIdsQuery + ") AS persisted_ids";

        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT object_id) FROM ( " + query +
                              " ) AS all_ids";
        command.Prepare();
        int count;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new GameException("IdManager: can't extract count ids");
            if (reader.GetInt32(0) != reader.GetInt32(1) && !distinct)
                throw new GameException("IdManager: there are duplicates in object ids");
            count = reader.GetInt32(0);
        }

        if (count == 0)
            return [];

        var result = new uint[count];
        Logger.Info($"{_name}: Extracting {count} used id's from data tables...");

        command.CommandText = query;
        command.Prepare();
        using (var reader = command.ExecuteReader())
        {
            var idx = 0;
            while (reader.Read())
            {
                result[idx] = reader.GetUInt32(0);
                idx++;
            }

            Logger.Info($"{_name}: Successfully extracted {idx} used id's from data tables.");
        }

        return result;
    }

    public void ReleaseId(uint usedObjectId)
    {
        lock (_lock)
        {
            if (_retainedIds.Contains(usedObjectId))
                return;

            var offset = (long)usedObjectId - _firstId;
            if (offset >= 0 && offset < _freeIds.Count)
            {
                var objectId = (int)offset;
                if (!_freeIds.Get(objectId))
                    return;
                _freeIds.Clear(objectId);
                if (_nextFreeId < 0 || _nextFreeId > objectId)
                    _nextFreeId = objectId;
                Interlocked.Increment(ref _freeIdCount);
            }
            else
                Logger.Error($"{_name}: release objectId {usedObjectId} failed");
        }
    }

    public void RetainId(uint usedObjectId)
    {
        lock (_lock)
        {
            var offset = (long)usedObjectId - _firstId;
            if (offset < 0 || offset >= _freeIdSize)
                throw new ArgumentOutOfRangeException(nameof(usedObjectId));
            var objectId = (int)offset;
            if (objectId >= _freeIds.Count)
                IncreaseBitSetCapacity(objectId + 1);
            if (!_freeIds.Get(objectId))
            {
                _freeIds.Set(objectId);
                Interlocked.Decrement(ref _freeIdCount);
            }

            _retainedIds.Add(usedObjectId);
            if (_nextFreeId == objectId)
                _nextFreeId = _freeIds.NextClear(objectId + 1);
        }
    }

    public void ReleaseId(IEnumerable<uint> usedObjectIds)
    {
        foreach (var id in usedObjectIds)
            ReleaseId(id);
    }

    public uint GetNextId()
    {
        lock (_lock)
        {
            while (_nextFreeId < 0)
            {
                _nextFreeId = _freeIds.NextClear(0);
                if (_nextFreeId < 0)
                {
                    if (_freeIds.Count < _freeIdSize)
                        IncreaseBitSetCapacity();
                    else
                        throw new GameException("Ran out of valid Id's.");
                }
            }

            var newId = _nextFreeId;
            _freeIds.Set(newId);
            Interlocked.Decrement(ref _freeIdCount);
            _nextFreeId = _freeIds.NextClear(newId + 1);
            return (uint)newId + _firstId;
        }
    }

    public uint[] GetNextId(int count)
    {
        var res = new uint[count];
        for (var i = 0; i < count; i++)
            res[i] = GetNextId();
        return res;
    }

    private void IncreaseBitSetCapacity()
    {
        var requestedSize = (int)Math.Min((long)_freeIds.Count + Math.Max(1, _freeIdSize / 10), _freeIdSize);
        var size = PrimeFinder.NextPrime(requestedSize);
        if (size > _freeIdSize)
            size = _freeIdSize;
        var newBitSet = new BitSet(size);
        newBitSet.Or(_freeIds);
        _freeIds = newBitSet;
    }

    private void IncreaseBitSetCapacity(int count)
    {
        var size = PrimeFinder.NextPrime(count);
        if (size > _freeIdSize)
            size = _freeIdSize;
        var newBitSet = new BitSet(size);
        newBitSet.Or(_freeIds);
        _freeIds = newBitSet;
    }
}
