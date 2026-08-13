namespace AAEmu.ContentStudio.Core.Models;

public sealed class ChangeDeletionPreview
{
    public string Path { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool CanDelete => Blockers.Count == 0;
    public int RetiredIdCount { get; set; }
    public List<string> Consequences { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
}

public sealed class ChangeDeletionResult
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int RetiredIdCount { get; set; }
    public int UpdatedChangeCount { get; set; }
}
