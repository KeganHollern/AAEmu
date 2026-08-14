namespace AAEmu.ContentStudio.Core.Models;

public sealed class CatalogRecord
{
    public string Table { get; set; } = string.Empty;
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string KindLabel { get; set; } = string.Empty;
    public bool CanChange { get; set; }
    public bool CanDuplicate { get; set; }
    public string? DuplicateNote { get; set; }
    public List<CatalogRecordField> Fields { get; set; } = [];
    public List<CatalogLocalizationField> Localizations { get; set; } = [];
    public List<CatalogRelatedSection> RelatedSections { get; set; } = [];
    public List<CatalogLinkedRecord> LinkedRecords { get; set; } = [];
    public List<CatalogGameplayLink> GameplayLinks { get; set; } = [];
}

public sealed class CatalogLinkedRecord
{
    public string Table { get; set; } = string.Empty;
    public uint SourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LinkTable { get; set; } = string.Empty;
    public uint LinkSourceId { get; set; }
    public string LinkColumn { get; set; } = string.Empty;
    public int ReferenceCount { get; set; }
    public bool Enabled { get; set; }
    public List<CatalogRecordField> Fields { get; set; } = [];
}

public sealed class CatalogGameplayLink
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string TargetTable { get; set; } = string.Empty;
    public uint TargetId { get; set; }
    public string ActionLabel { get; set; } = "Open connected entry";
    public List<CatalogGameplayFact> Facts { get; set; } = [];
}

public sealed class CatalogGameplayFact
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Help { get; set; }
}

public sealed class CatalogRelatedSection
{
    public string Table { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OwnerColumn { get; set; } = string.Empty;
    public bool IsEquipmentTemplate { get; set; }
    public List<CatalogRelatedRow> Rows { get; set; } = [];
}

public sealed class CatalogRelatedRow
{
    public uint Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<CatalogRecordField> Fields { get; set; } = [];
}

public sealed class CatalogRecordField
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string? Help { get; set; }
    public string? Value { get; set; }
    public bool IsNull { get; set; }
    public bool IsBoolean { get; set; }
    public bool IsEssential { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsEditable { get; set; } = true;
    public string? ReferenceTable { get; set; }
}

public sealed class CatalogLocalizationField
{
    public string Field { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public Dictionary<string, string> Values { get; set; } = [];
}

public sealed class AbilityGraph
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<AbilitySkillSummary> Skills { get; set; } = [];
}

public sealed class AbilitySkillSummary
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int RequiredAbilityLevel { get; set; }
    public int ManaCost { get; set; }
    public int CastTime { get; set; }
    public int Cooldown { get; set; }
    public bool Visible { get; set; }
}

public enum RecordChangeMode
{
    Modify,
    Duplicate
}

public sealed class RecordDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public RecordChangeMode Mode { get; set; }
    public string Table { get; set; } = string.Empty;
    public uint SourceId { get; set; }
    public uint Id { get; set; }
    public Dictionary<string, string?> Values { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> Localizations { get; set; } = [];
    public Dictionary<string, uint> LocalizationRowIds { get; set; } = [];
    public List<RecordChildClone> Children { get; set; } = [];
    public List<RecordLinkedClone> LinkedClones { get; set; } = [];
}

public sealed class RecordChildClone
{
    public string Table { get; set; } = string.Empty;
    public string OwnerColumn { get; set; } = string.Empty;
    public uint SourceId { get; set; }
    public uint Id { get; set; }
    public Dictionary<string, string?> Values { get; set; } = [];
}

public sealed class RecordLinkedClone
{
    public string Table { get; set; } = string.Empty;
    public uint SourceId { get; set; }
    public uint Id { get; set; }
    public string LinkTable { get; set; } = string.Empty;
    public uint LinkSourceId { get; set; }
    public string LinkColumn { get; set; } = string.Empty;
    public Dictionary<string, string?> Values { get; set; } = [];
}

public sealed class RecordDraftRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public string BaselinePath { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public uint SourceId { get; set; }
    public RecordChangeMode Mode { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public Dictionary<string, string?> Values { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> Localizations { get; set; } = [];
    public List<RecordChildDraft> Children { get; set; } = [];
    public List<RecordLinkedDraft> LinkedRecords { get; set; } = [];
}

public sealed class RecordChildDraft
{
    public string Table { get; set; } = string.Empty;
    public string OwnerColumn { get; set; } = string.Empty;
    public uint SourceId { get; set; }
    public Dictionary<string, string?> Values { get; set; } = [];
}

public sealed class RecordLinkedDraft
{
    public string Table { get; set; } = string.Empty;
    public uint SourceId { get; set; }
    public string LinkTable { get; set; } = string.Empty;
    public uint LinkSourceId { get; set; }
    public string LinkColumn { get; set; } = string.Empty;
    public Dictionary<string, string?> Values { get; set; } = [];
}

public sealed class RecordDraftResult
{
    public string Key { get; set; } = string.Empty;
    public uint Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public int RelatedRowsCopied { get; set; }
}
