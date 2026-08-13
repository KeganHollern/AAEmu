namespace AAEmu.ContentStudio.Core.Models;

public sealed record TableColumn(string Name, string Type, bool NotNull, string? DefaultValue, int PrimaryKeyOrder);
public sealed record TableSchema(string Name, long RowCount, IReadOnlyList<TableColumn> Columns);
public sealed record ItemSearchResult(uint Id, string Name, uint CategoryId, int Price);
public sealed record RecipeLookupResult(uint Id, string Name, int MaterialCount, int ProductCount, uint RequiredDoodadId);
public sealed record WorkbenchLookupResult(uint Id, string Name, string Model, int GroupCount, int FunctionCount, int RecipeCount);
public sealed record ItemRecipeRelationship(uint RecipeId, string RecipeName, int Amount, uint RequiredDoodadId);

public sealed class ItemGameplayProfile
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ItemLevel { get; set; }
    public int RequiredLevel { get; set; }
    public bool Gradable { get; set; }
    public int FixedGrade { get; set; }
    public string? GearKind { get; set; }
    public string? EquipmentType { get; set; }
    public uint SlotTypeId { get; set; }
    public bool Enchantable { get; set; }
    public bool Repairable { get; set; }
    public int DurabilityMultiplier { get; set; }
    public int AttackSpeed { get; set; }
    public int DamageScale { get; set; }
    public int MaximumRange { get; set; }
    public int ArmorBasisPoints { get; set; }
    public int MagicResistanceBasisPoints { get; set; }
    public uint AttributeModifierSetId { get; set; }
    public IReadOnlyList<ItemStatWeight> StatWeights { get; set; } = [];
    public IReadOnlyList<ItemLinkedEffect> Effects { get; set; } = [];
    public ItemEquipmentSet? EquipmentSet { get; set; }
    public bool IsEquipment => !string.IsNullOrWhiteSpace(GearKind);
}

public sealed record ItemStatWeight(string Name, int Weight, int Percentage);
public sealed record EquipmentSetPiece(uint Id, string Name, string GearKind);

public sealed class ItemLinkedEffect
{
    public string Source { get; set; } = string.Empty;
    public string TargetTable { get; set; } = string.Empty;
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IReadOnlyList<string> Facts { get; set; } = [];
}

public sealed class ItemEquipmentSet
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IReadOnlyList<EquipmentSetPiece> Pieces { get; set; } = [];
    public IReadOnlyList<ItemEquipmentSetBonus> Bonuses { get; set; } = [];
}

public sealed class ItemEquipmentSetBonus
{
    public int RequiredPieces { get; set; }
    public ItemLinkedEffect? Buff { get; set; }
    public ItemLinkedEffect? Proc { get; set; }
}

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
public sealed record RecipeProduct(uint RowId, uint ItemId, string ItemName, int Amount, int Rate, bool ShowLowerCrafts, bool UseGrade, uint ItemGradeId);

public sealed class RecipeGraph
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
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
