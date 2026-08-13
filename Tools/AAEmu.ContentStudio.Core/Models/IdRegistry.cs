using System.Text.Json.Serialization;

namespace AAEmu.ContentStudio.Core.Models;

public sealed class IdRegistry
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "../../schemas/id-registry.schema.json";
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, IdRange> Ranges { get; set; } = [];
    public Dictionary<string, Dictionary<string, uint>> Allocations { get; set; } = [];
    public Dictionary<string, Dictionary<string, uint>> Tombstones { get; set; } = [];
}

public sealed class IdRange
{
    public uint Start { get; set; }
    public uint End { get; set; }
}

public sealed record IdAllocation(string Table, string Key, uint Id);
