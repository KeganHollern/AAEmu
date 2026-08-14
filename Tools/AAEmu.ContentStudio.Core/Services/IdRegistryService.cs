using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class IdRegistryService
{
    public IdAllocation Allocate(IdRegistry registry, string compactPath, string table, string key)
    {
        NormalizeComparers(registry);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        var canonicalTable = SqliteRowService.ResolveTableName(connection, null, table)
            ?? throw new ContentStudioException($"Table '{table}' does not exist in this compact database.");
        CanonicalizeTableKeys(registry, canonicalTable);
        table = canonicalTable;

        if (registry.Allocations.TryGetValue(table, out var existing) && existing.TryGetValue(key, out var existingId))
        {
            return new IdAllocation(table, key, existingId);
        }

        if (!registry.Ranges.TryGetValue(table, out var range))
        {
            throw new ContentStudioException($"No custom ID range is registered for table '{table}'.");
        }

        var used = new HashSet<uint>();
        foreach (var values in registry.Allocations.Where(pair => pair.Key.Equals(table, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
        {
            used.UnionWith(values.Values);
        }
        foreach (var values in registry.Tombstones.Where(pair => pair.Key.Equals(table, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
        {
            used.UnionWith(values.Values);
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {BaselineVerifier.QuoteIdentifier(table)} WHERE id BETWEEN @start AND @end;";
        command.Parameters.AddWithValue("@start", range.Start);
        command.Parameters.AddWithValue("@end", range.End);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            used.Add(Convert.ToUInt32(reader.GetInt64(0)));
        }

        var candidate = (ulong)range.Start;
        while (candidate <= range.End && used.Contains((uint)candidate))
        {
            candidate++;
        }

        if (candidate > range.End)
        {
            throw new ContentStudioException($"The custom ID range for '{table}' is exhausted ({range.Start}-{range.End}).");
        }
        var id = (uint)candidate;

        if (!registry.Allocations.TryGetValue(table, out var tableAllocations))
        {
            tableAllocations = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            registry.Allocations[table] = tableAllocations;
        }
        tableAllocations.Add(key, id);
        return new IdAllocation(table, key, id);
    }

    internal static void NormalizeComparers(IdRegistry registry)
    {
        registry.Ranges = NormalizeRanges(registry.Ranges);
        registry.Allocations = NormalizeBuckets(registry.Allocations, "allocation");
        registry.Tombstones = NormalizeBuckets(registry.Tombstones, "tombstone");
    }

    internal static void CanonicalizeTableKeys(IdRegistry registry, string canonicalTable)
    {
        RenameKey(registry.Ranges, canonicalTable);
        RenameKey(registry.Allocations, canonicalTable);
        RenameKey(registry.Tombstones, canonicalTable);
    }

    internal static void AddTombstone(IdRegistry registry, string table, string allocationKey, uint id)
    {
        NormalizeComparers(registry);
        CanonicalizeTableKeys(registry, table);
        if (!registry.Tombstones.TryGetValue(table, out var tombstones))
        {
            tombstones = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            registry.Tombstones[table] = tombstones;
        }
        if (tombstones.Values.Contains(id)) return;

        var tombstoneKey = allocationKey;
        if (tombstones.TryGetValue(tombstoneKey, out var existingId) && existingId != id)
        {
            tombstoneKey = $"{allocationKey}:retired:{id}";
            var suffix = 2;
            while (tombstones.TryGetValue(tombstoneKey, out existingId) && existingId != id)
            {
                tombstoneKey = $"{allocationKey}:retired:{id}:{suffix++}";
            }
        }
        tombstones[tombstoneKey] = id;
    }

    private static Dictionary<string, IdRange> NormalizeRanges(Dictionary<string, IdRange> source)
    {
        var result = new Dictionary<string, IdRange>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, range) in source)
        {
            if (result.TryGetValue(table, out var existing) && (existing.Start != range.Start || existing.End != range.End))
            {
                throw new ContentStudioException($"The ID registry contains conflicting custom ranges for table '{table}'.");
            }
            result.TryAdd(table, range);
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, uint>> NormalizeBuckets(
        Dictionary<string, Dictionary<string, uint>> source,
        string kind)
    {
        var result = new Dictionary<string, Dictionary<string, uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, values) in source)
        {
            if (!result.TryGetValue(table, out var normalizedValues))
            {
                normalizedValues = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                result[table] = normalizedValues;
            }
            foreach (var (key, id) in values)
            {
                if (normalizedValues.TryGetValue(key, out var existing) && existing != id)
                {
                    throw new ContentStudioException($"The ID registry contains conflicting {kind}s for '{table}/{key}'.");
                }
                normalizedValues.TryAdd(key, id);
            }
        }
        return result;
    }

    private static void RenameKey<T>(Dictionary<string, T> values, string canonicalTable)
    {
        var existingKey = values.Keys.FirstOrDefault(table => table.Equals(canonicalTable, StringComparison.OrdinalIgnoreCase));
        if (existingKey is null || existingKey.Equals(canonicalTable, StringComparison.Ordinal)) return;
        var value = values[existingKey];
        values.Remove(existingKey);
        values[canonicalTable] = value;
    }
}
