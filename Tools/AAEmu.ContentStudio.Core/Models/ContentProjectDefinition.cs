namespace AAEmu.ContentStudio.Core.Models;

public sealed class ContentProjectDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TargetBaseline { get; set; } = string.Empty;
    public string DefaultLanguage { get; set; } = "en_us";
    public string IdRegistry { get; set; } = "id-registry.json";
    public string[] Recipes { get; set; } = ["recipes/*.json"];
    public string[] Workbenches { get; set; } = ["workbenches/*.json"];
    public string[] RawSql { get; set; } = ["raw-sql/*.sql"];
}

public sealed class LoadedContentProject
{
    public required string ProjectDirectory { get; init; }
    public required ContentProjectDefinition Definition { get; init; }
    public required IdRegistry Registry { get; init; }
    public IReadOnlyList<RecipeDefinition> Recipes { get; init; } = [];
    public IReadOnlyList<WorkbenchDefinition> Workbenches { get; init; } = [];
    public IReadOnlyList<string> RawSqlFiles { get; init; } = [];
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
}
