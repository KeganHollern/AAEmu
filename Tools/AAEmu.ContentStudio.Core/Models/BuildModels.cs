namespace AAEmu.ContentStudio.Core.Models;

public sealed class ContentBuildRequest
{
    public required string BaselinePath { get; init; }
    public required string BaselineDescriptorPath { get; init; }
    public required string ProjectPath { get; init; }
    public required string OutputDirectory { get; init; }
    public bool KeepStagingOnFailure { get; init; }
}

public sealed class ContentBuildManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = string.Empty;
    public DateTimeOffset BuiltAtUtc { get; set; }
    public string BaselineKey { get; set; } = string.Empty;
    public string BaselineSha256 { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public Dictionary<string, string> SourceHashes { get; set; } = [];
    public string ArtifactPath { get; set; } = string.Empty;
    public string ArtifactSha256 { get; set; } = string.Empty;
    public string AuditSqlPath { get; set; } = string.Empty;
    public long ArtifactLength { get; set; }
    public int RecipeCount { get; set; }
    public int WorkbenchCount { get; set; }
    public int AssertionCount { get; set; }
    public int TowerDefenseEventCount { get; set; }
    public string? TowerDefenseBundlePath { get; set; }
    public string? TowerDefenseBundleSha256 { get; set; }
    public ValidationReport Validation { get; set; } = new();
    public List<ContentChange> Changes { get; set; } = [];
}

public sealed record ContentChange(string EntityType, string Key, uint Id, string Operation, string Summary);

public sealed class ContentBuildResult
{
    public required string ArtifactPath { get; init; }
    public required string ManifestPath { get; init; }
    public required string ReportPath { get; init; }
    public required string AuditSqlPath { get; init; }
    public string? TowerDefenseBundlePath { get; init; }
    public required ContentBuildManifest Manifest { get; init; }
}

public sealed class StudioConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string BaselinePath { get; set; } = string.Empty;
    public string BaselineDescriptorPath { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public Dictionary<string, DeploymentTarget> Targets { get; set; } = [];
}

public sealed class DeploymentTarget
{
    public string Path { get; set; } = string.Empty;
    public string BackupDirectory { get; set; } = string.Empty;
}

public sealed class DeploymentManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset DeployedAtUtc { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string ArtifactPath { get; set; } = string.Empty;
    public string ArtifactSha256 { get; set; } = string.Empty;
    public string? PreviousSha256 { get; set; }
    public string? BackupPath { get; set; }
}
