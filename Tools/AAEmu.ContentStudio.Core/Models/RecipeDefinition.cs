namespace AAEmu.ContentStudio.Core.Models;

public sealed class RecipeDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = string.Empty;
    public uint Id { get; set; }
    public Dictionary<string, string> Names { get; set; } = [];
    public Dictionary<string, string> Descriptions { get; set; } = [];
    public uint[] CraftPackIds { get; set; } = [];
    public uint SkillId { get; set; }
    public SkillCloneDefinition? SkillClone { get; set; }
    public uint RequiredDoodadId { get; set; }
    public uint ActabilityCategoryId { get; set; }
    public int ActabilityLimit { get; set; }
    public int CastDelay { get; set; }
    public uint WorldInteractionId { get; set; }
    public int RecommendLevel { get; set; }
    public int VisibleOrder { get; set; }
    public bool NeedBind { get; set; }
    public bool ShowUpperCrafts { get; set; }
    public RecipeRowIds RowIds { get; set; } = new();
    public List<RecipeMaterialDefinition> Materials { get; set; } = [];
    public List<RecipeProductDefinition> Products { get; set; } = [];
}

public sealed class SkillCloneDefinition
{
    public uint SourceId { get; set; }
    public uint Id { get; set; }
    public int? LaborCost { get; set; }
    public int? CastingTime { get; set; }
    public uint[] SkillEffectRowIds { get; set; } = [];
}

public sealed class RecipeRowIds
{
    public Dictionary<string, uint> Localization { get; set; } = [];
    public uint[] CraftPackLinks { get; set; } = [];
}

public sealed class RecipeMaterialDefinition
{
    public uint Id { get; set; }
    public uint ItemId { get; set; }
    public int Amount { get; set; }
    public bool MainGrade { get; set; }
    public int RequiredGrade { get; set; }
}

public sealed class RecipeProductDefinition
{
    public uint Id { get; set; }
    public uint ItemId { get; set; }
    public int Amount { get; set; }
    public int Rate { get; set; } = 100;
    public bool ShowLowerCrafts { get; set; }
    public bool UseGrade { get; set; }
    public uint ItemGradeId { get; set; }
}
