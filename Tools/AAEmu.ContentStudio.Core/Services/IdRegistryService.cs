using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class IdRegistryService
{
    public IdAllocation Allocate(IdRegistry registry, string compactPath, string table, string key)
    {
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

        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
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
}
