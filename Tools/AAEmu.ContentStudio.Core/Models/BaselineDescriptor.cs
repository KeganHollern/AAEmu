namespace AAEmu.ContentStudio.Core.Models;

public sealed class BaselineDescriptor
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = string.Empty;
    public string ClientBuild { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Length { get; set; }
    public int TableCount { get; set; }
    public Dictionary<string, string[]> RequiredTables { get; set; } = [];
}
