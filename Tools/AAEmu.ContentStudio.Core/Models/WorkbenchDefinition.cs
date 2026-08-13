namespace AAEmu.ContentStudio.Core.Models;

public sealed class WorkbenchDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = string.Empty;
    public uint Id { get; set; }
    public uint SourceDoodadId { get; set; }
    public Dictionary<string, string> Names { get; set; } = [];
    public string? ModelOverride { get; set; }
    public WorkbenchCraftPackDefinition CraftPack { get; set; } = new();
    public uint[] RecipeIds { get; set; } = [];
    public WorkbenchRowIds RowIds { get; set; } = new();
}

public sealed class WorkbenchCraftPackDefinition
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class WorkbenchRowIds
{
    public Dictionary<uint, uint> FunctionGroups { get; set; } = [];
    public Dictionary<uint, uint> Functions { get; set; } = [];
    public Dictionary<uint, uint> PhaseFunctions { get; set; } = [];
    public Dictionary<uint, uint> CraftPackPayloads { get; set; } = [];
    public Dictionary<string, uint> Localization { get; set; } = [];
    public uint[] CraftPackLinks { get; set; } = [];
}
