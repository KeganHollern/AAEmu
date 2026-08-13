namespace AAEmu.ContentStudio.Core.Models;

public sealed record TableColumn(string Name, string Type, bool NotNull, string? DefaultValue, int PrimaryKeyOrder);
public sealed record TableSchema(string Name, long RowCount, IReadOnlyList<TableColumn> Columns);
public sealed record ItemSearchResult(uint Id, string Name, uint CategoryId, int Price);

public sealed class CatalogSearchResponse
{
    public string Query { get; set; } = string.Empty;
    public bool UsedFuzzyMatching { get; set; }
    public IReadOnlyList<CatalogSearchResult> Results { get; set; } = [];
}

public sealed class CatalogSearchResult
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "other";
    public string KindLabel { get; set; } = "Other";
    public string Table { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string? Context { get; set; }
    public int Score { get; set; }
    public string InspectKind { get; set; } = "generic";
    public string? RelatedTable { get; set; }
    public uint? RelatedId { get; set; }
}

public sealed record RecipeMaterial(uint RowId, uint ItemId, string ItemName, int Amount, bool MainGrade, int RequiredGrade);
public sealed record RecipeProduct(uint RowId, uint ItemId, string ItemName, int Amount, int Rate, bool UseGrade, uint ItemGradeId);

public sealed class RecipeGraph
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint SkillId { get; set; }
    public int LaborCost { get; set; }
    public int CastingTime { get; set; }
    public uint RequiredDoodadId { get; set; }
    public IReadOnlyList<uint> CraftPackIds { get; set; } = [];
    public IReadOnlyList<RecipeMaterial> Materials { get; set; } = [];
    public IReadOnlyList<RecipeProduct> Products { get; set; } = [];
}

public sealed record WorkbenchFunction(
    uint RowId,
    uint GroupId,
    string FunctionType,
    uint ActualFunctionId,
    int NextPhase,
    uint SkillId,
    uint? CraftPackId);

public sealed record WorkbenchFunctionGroup(
    uint Id,
    uint KindId,
    string Model,
    IReadOnlyList<WorkbenchFunction> Functions);

public sealed class WorkbenchGraph
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public IReadOnlyList<WorkbenchFunctionGroup> Groups { get; set; } = [];
    public IReadOnlyList<uint> CraftPackIds { get; set; } = [];
    public IReadOnlyList<uint> RecipeIds { get; set; } = [];
}
