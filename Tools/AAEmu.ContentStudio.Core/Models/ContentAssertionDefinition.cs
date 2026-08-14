namespace AAEmu.ContentStudio.Core.Models;

/// <summary>
/// A read-only scalar query that must return the expected value in a compiled artifact.
/// Assertions let a content project describe release invariants which are not owned by
/// any single edited row, such as a complete skill chain or level table segment.
/// </summary>
public sealed class ContentAssertionDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Expected { get; set; } = string.Empty;
}
