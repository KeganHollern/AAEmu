namespace AAEmu.ContentStudio.Core.Models;

public sealed class RecipeScaffoldRequest
{
    public required string ProjectPath { get; init; }
    public required string BaselinePath { get; init; }
    public required string Key { get; init; }
    public required uint SourceRecipeId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint[]? CraftPackIds { get; init; }
    public bool CloneSkill { get; init; }
    public RecipeDefinition? Draft { get; init; }
    public bool DryRun { get; init; }
}

public sealed class WorkbenchScaffoldRequest
{
    public required string ProjectPath { get; init; }
    public required string BaselinePath { get; init; }
    public required string Key { get; init; }
    public required uint SourceDoodadId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint[] RecipeIds { get; init; } = [];
    public string? ModelOverride { get; init; }
    public string? CraftPackName { get; init; }
    public bool DryRun { get; init; }
}

public sealed record ScaffoldResult(string Path, string Key, uint Id, IReadOnlyList<IdAllocation> Allocations)
{
    public string? GmCommand { get; init; }
    public bool DryRun { get; init; }
}

public sealed record DatabaseTableDiff(string Table, long BaselineRows, long ArtifactRows, long AddedRows);

public sealed class DatabaseDiffReport
{
    public string BaselinePath { get; set; } = string.Empty;
    public string ArtifactPath { get; set; } = string.Empty;
    public List<DatabaseTableDiff> Tables { get; set; } = [];
}
