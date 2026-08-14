using System.Text;
using System.Globalization;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class RecordScaffoldService
{
    private readonly ProjectRepository _repository = new();
    private readonly IdRegistryService _ids = new();
    private readonly ManifestService _manifests = new();
    private readonly Action<int, string>? _beforeApply;

    public RecordScaffoldService()
    {
    }

    internal RecordScaffoldService(Action<int, string> beforeApply)
    {
        _beforeApply = beforeApply;
    }

    public RecordDraftResult Save(RecordDraftRequest request)
    {
        lock (AtomicFile.SyncRoot)
        {
            return SaveCore(request);
        }
    }

    private RecordDraftResult SaveCore(RecordDraftRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Table))
        {
            throw new ContentStudioException("Choose a valid entry before saving changes.");
        }
        using (var baseline = CompactConnectionFactory.OpenReadOnly(request.BaselinePath))
        {
            CanonicalizeRequest(baseline, request);
            if (!SqliteRowService.Exists(baseline, null, request.Table, request.SourceId))
                throw new ContentStudioException($"The source {request.Table} entry {request.SourceId} does not exist.");
        }
        if (request.Mode == RecordChangeMode.Duplicate && request.Table is "crafts" or "doodad_almighties")
        {
            throw new ContentStudioException(request.Table == "crafts"
                ? "Recipes have connected ingredients, products, and skills. Use Recipe maker so the complete recipe is copied."
                : "Workbenches have connected function graphs. Use Workbench maker so the complete workbench is copied.");
        }

        var project = _repository.LoadProject(request.ProjectPath);
        var registryPath = Path.GetFullPath(Path.Combine(project.ProjectDirectory, project.Definition.IdRegistry));
        _beforeApply?.Invoke(-2, registryPath);
        var registryContents = File.ReadAllText(registryPath);
        var registry = ContentStudioJson.Deserialize<IdRegistry>(registryContents, registryPath);
        IdRegistryService.NormalizeComparers(registry);
        EnsureRange(registry, request.Table);
        var baseSlug = Slugify(string.IsNullOrWhiteSpace(request.DisplayName) ? $"{request.Table}-{request.SourceId}" : request.DisplayName);
        var key = FindAvailableKey(project.ProjectDirectory, $"record.{baseSlug}");
        var definition = new RecordDefinition
        {
            Key = key,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? $"{CatalogRecordService.FriendlyName(request.Table)} {request.SourceId}" : request.DisplayName.Trim(),
            Mode = request.Mode,
            Table = request.Table,
            SourceId = request.SourceId,
            Id = request.SourceId,
            Values = new Dictionary<string, string?>(request.Values, StringComparer.OrdinalIgnoreCase),
            Localizations = request.Localizations.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
        };
        definition.Values.Remove("id");

        if (request.Mode == RecordChangeMode.Modify)
        {
            using var connection = CompactConnectionFactory.OpenReadOnly(request.BaselinePath);
            definition.Values = KeepChangedValues(connection, request.Table, request.SourceId, definition.Values);
            definition.Localizations = KeepChangedLocalizations(connection, request.Table, request.SourceId, definition.Localizations);
        }

        if (request.Mode == RecordChangeMode.Duplicate)
        {
            definition.Id = Allocate(registry, request, key, request.Table, "row");
            foreach (var field in definition.Localizations.Keys)
            {
                definition.LocalizationRowIds[field] = Allocate(registry, request, key, "localized_texts", $"localization:{field}");
            }
            if (request.Table.Equals("skills", StringComparison.OrdinalIgnoreCase))
            {
                AddOwnedSkillRows(registry, request, definition);
            }
            else
            {
                AddOwnedRows(registry, request, definition);
            }
        }
        else
        {
            using var connection = CompactConnectionFactory.OpenReadOnly(request.BaselinePath);
            foreach (var field in definition.Localizations.Keys.Where(field => !LocalizationExists(connection, request.Table, request.SourceId, field)))
            {
                definition.LocalizationRowIds[field] = Allocate(registry, request, key, "localized_texts", $"localization:{field}");
            }
            definition.Children = request.Children.Select(child => new RecordChildClone
            {
                Table = child.Table,
                OwnerColumn = child.OwnerColumn,
                SourceId = child.SourceId,
                Id = child.SourceId,
                Values = KeepChangedValues(connection, child.Table, child.SourceId, CleanChildValues(child))
            }).ToList();
        }

        foreach (var linked in request.LinkedRecords)
        {
            EnsureRange(registry, linked.Table);
            definition.LinkedClones.Add(new RecordLinkedClone
            {
                Table = linked.Table,
                SourceId = linked.SourceId,
                Id = Allocate(registry, request, key, linked.Table, $"linked:{linked.Table}:{linked.SourceId}:{linked.LinkTable}:{linked.LinkSourceId}"),
                LinkTable = linked.LinkTable,
                LinkSourceId = linked.LinkSourceId,
                LinkColumn = linked.LinkColumn,
                Values = new Dictionary<string, string?>(linked.Values, StringComparer.OrdinalIgnoreCase)
            });
        }

        var directory = Path.Combine(project.ProjectDirectory, "records");
        var path = Path.Combine(directory, key + ".json");
        Directory.CreateDirectory(directory);
        var mutations = new List<FileMutation>
        {
            new(path, null, Normalize(ContentStudioJson.Serialize(definition)))
        };
        var updatedRegistry = Normalize(ContentStudioJson.Serialize(registry));
        if (!updatedRegistry.Equals(registryContents, StringComparison.Ordinal))
        {
            mutations.Add(new FileMutation(registryPath, registryContents, updatedRegistry));
        }
        ApplyMutations(mutations);
        return new RecordDraftResult { Key = key, Id = definition.Id, Path = path, RelatedRowsCopied = definition.Children.Count };
    }

    public RecordDraftResult Update(string path, RecordDefinition definition, RecordDraftRequest request, string expectedVersion)
    {
        lock (AtomicFile.SyncRoot)
        {
            return UpdateCore(path, definition, request, expectedVersion);
        }
    }

    private RecordDraftResult UpdateCore(string path, RecordDefinition definition, RecordDraftRequest request, string expectedVersion)
    {
        var snapshot = _manifests.ReadSnapshot(path);
        if (!snapshot.Version.Equals(expectedVersion, StringComparison.Ordinal))
        {
            throw new ContentStudioException("This saved change was updated outside this editor. Reload it to see the newest work before saving your changes.");
        }
        definition = ContentStudioJson.Deserialize<RecordDefinition>(snapshot.Contents, snapshot.Path);
        var project = _repository.LoadProject(request.ProjectPath);
        var registryPath = Path.GetFullPath(Path.Combine(project.ProjectDirectory, project.Definition.IdRegistry));
        _beforeApply?.Invoke(-2, registryPath);
        var registryContents = File.ReadAllText(registryPath);
        var registry = ContentStudioJson.Deserialize<IdRegistry>(registryContents, registryPath);
        IdRegistryService.NormalizeComparers(registry);
        using var baseline = CompactConnectionFactory.OpenReadOnly(request.BaselinePath);
        CanonicalizeRequest(baseline, request);
        definition.Table = RequireTable(baseline, definition.Table);
        if (!definition.Table.Equals(request.Table, StringComparison.OrdinalIgnoreCase) || definition.SourceId != request.SourceId)
        {
            throw new ContentStudioException("The saved entry no longer matches the entry being edited. Reload it before saving changes.");
        }
        CanonicalizeDefinitionGraph(baseline, definition);
        definition.DisplayName = request.DisplayName;
        definition.Values = new Dictionary<string, string?>(request.Values, StringComparer.OrdinalIgnoreCase);
        definition.Values.Remove("id");
        definition.Localizations = request.Localizations.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        if (definition.Mode == RecordChangeMode.Modify)
        {
            definition.Values = KeepChangedValues(baseline, definition.Table, definition.SourceId, definition.Values);
            definition.Localizations = KeepChangedLocalizations(baseline, definition.Table, definition.SourceId, definition.Localizations);
        }
        foreach (var field in definition.Localizations.Keys.Where(field =>
                     !LocalizationExists(baseline, definition.Table, definition.SourceId, field) &&
                     !definition.LocalizationRowIds.Keys.Any(key => key.Equals(field, StringComparison.OrdinalIgnoreCase))))
        {
            definition.LocalizationRowIds[field] = Allocate(registry, request, definition.Key, "localized_texts", $"localization:{field}");
        }
        foreach (var draft in request.Children)
        {
            var child = definition.Children.FirstOrDefault(value => value.Table.Equals(draft.Table, StringComparison.OrdinalIgnoreCase) && value.SourceId == draft.SourceId);
            if (child is not null)
            {
                child.Values = CleanChildValues(draft);
                if (definition.Mode == RecordChangeMode.Modify)
                    child.Values = KeepChangedValues(baseline, child.Table, child.SourceId, child.Values);
            }
        }
        var removedLinks = definition.LinkedClones
            .Where(linked => !request.LinkedRecords.Any(draft => SameLink(linked, draft)))
            .ToList();
        definition.LinkedClones.RemoveAll(removedLinks.Contains);
        foreach (var removedLink in removedLinks)
        {
            RetireLinkedAllocation(registry, definition.Key, removedLink);
        }
        foreach (var draft in request.LinkedRecords)
        {
            var linked = definition.LinkedClones.FirstOrDefault(value => SameLink(value, draft));
            if (linked is not null)
            {
                linked.Values = new Dictionary<string, string?>(draft.Values, StringComparer.OrdinalIgnoreCase);
                continue;
            }
            EnsureRange(registry, draft.Table);
            definition.LinkedClones.Add(new RecordLinkedClone
            {
                Table = draft.Table,
                SourceId = draft.SourceId,
                Id = Allocate(registry, request, definition.Key, draft.Table, $"linked:{draft.Table}:{draft.SourceId}:{draft.LinkTable}:{draft.LinkSourceId}"),
                LinkTable = draft.LinkTable,
                LinkSourceId = draft.LinkSourceId,
                LinkColumn = draft.LinkColumn,
                Values = new Dictionary<string, string?>(draft.Values, StringComparer.OrdinalIgnoreCase)
            });
        }
        var mutations = new List<FileMutation>
        {
            new(snapshot.Path, snapshot.Contents, Normalize(ContentStudioJson.Serialize(definition)))
        };
        var updatedRegistry = Normalize(ContentStudioJson.Serialize(registry));
        if (!updatedRegistry.Equals(registryContents, StringComparison.Ordinal))
        {
            mutations.Add(new FileMutation(registryPath, registryContents, updatedRegistry));
        }
        ApplyMutations(mutations);
        return new RecordDraftResult { Key = definition.Key, Id = definition.Id, Path = path, RelatedRowsCopied = definition.Children.Count };
    }

    private static bool SameLink(RecordLinkedClone linked, RecordLinkedDraft draft) =>
        linked.Table.Equals(draft.Table, StringComparison.OrdinalIgnoreCase) &&
        linked.LinkTable.Equals(draft.LinkTable, StringComparison.OrdinalIgnoreCase) &&
        linked.LinkSourceId == draft.LinkSourceId &&
        linked.LinkColumn.Equals(draft.LinkColumn, StringComparison.OrdinalIgnoreCase);

    private static void CanonicalizeRequest(SqliteConnection connection, RecordDraftRequest request)
    {
        request.Table = RequireTable(connection, request.Table);
        request.Values = CanonicalizeValues(connection, request.Table, request.Values);
        request.Localizations = CanonicalizeLocalizations(connection, request.Table, request.SourceId, request.Localizations);
        foreach (var child in request.Children)
        {
            child.Table = RequireTable(connection, child.Table);
            child.OwnerColumn = RequireColumn(connection, child.Table, child.OwnerColumn);
            child.Values = CanonicalizeValues(connection, child.Table, child.Values);
        }
        foreach (var linked in request.LinkedRecords)
        {
            linked.Table = RequireTable(connection, linked.Table);
            linked.Values = CanonicalizeValues(connection, linked.Table, linked.Values);
            linked.LinkTable = RequireTable(connection, linked.LinkTable);
            linked.LinkColumn = RequireColumn(connection, linked.LinkTable, linked.LinkColumn);
        }
    }

    private static void CanonicalizeDefinitionGraph(SqliteConnection connection, RecordDefinition definition)
    {
        foreach (var child in definition.Children)
        {
            child.Table = RequireTable(connection, child.Table);
            child.OwnerColumn = RequireColumn(connection, child.Table, child.OwnerColumn);
            child.Values = CanonicalizeValues(connection, child.Table, child.Values);
        }
        foreach (var linked in definition.LinkedClones)
        {
            linked.Table = RequireTable(connection, linked.Table);
            linked.Values = CanonicalizeValues(connection, linked.Table, linked.Values);
            linked.LinkTable = RequireTable(connection, linked.LinkTable);
            linked.LinkColumn = RequireColumn(connection, linked.LinkTable, linked.LinkColumn);
        }
    }

    private static Dictionary<string, string?> CanonicalizeValues(
        SqliteConnection connection,
        string table,
        IReadOnlyDictionary<string, string?> values)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (requestedColumn, value) in values)
        {
            var column = RequireColumn(connection, table, requestedColumn);
            if (!result.TryAdd(column, value))
            {
                throw new ContentStudioException($"Column '{column}' was provided more than once.");
            }
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> CanonicalizeLocalizations(
        SqliteConnection connection,
        string table,
        uint id,
        IReadOnlyDictionary<string, Dictionary<string, string>> localizations)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (requestedField, requestedLanguages) in localizations)
        {
            _ = BaselineVerifier.QuoteIdentifier(requestedField);
            var field = ResolveLocalizationField(connection, table, id, requestedField);
            var languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (requestedLanguage, value) in requestedLanguages)
            {
                var language = LocalizationCompiler.Languages.FirstOrDefault(candidate => candidate.Equals(requestedLanguage, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ContentStudioException($"Unsupported localization language '{requestedLanguage}'.");
                if (!languages.TryAdd(language, value))
                {
                    throw new ContentStudioException($"Localization language '{language}' was provided more than once.");
                }
            }
            if (!result.TryAdd(field, languages))
            {
                throw new ContentStudioException($"Localized field '{field}' was provided more than once.");
            }
        }
        return result;
    }

    private static string ResolveLocalizationField(SqliteConnection connection, string table, uint id, string requestedField)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tbl_column_name FROM localized_texts WHERE tbl_name = @table COLLATE NOCASE AND tbl_column_name = @field COLLATE NOCASE ORDER BY CASE WHEN idx = @id THEN 0 ELSE 1 END, id LIMIT 1;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@field", requestedField);
        command.Parameters.AddWithValue("@id", id);
        var existing = command.ExecuteScalar();
        if (existing is not null and not DBNull) return Convert.ToString(existing)!;
        return SqliteRowService.ResolveColumnName(connection, null, table, requestedField) ?? requestedField.ToLowerInvariant();
    }

    private static string RequireTable(SqliteConnection connection, string requestedTable) =>
        SqliteRowService.ResolveTableName(connection, null, requestedTable)
        ?? throw new ContentStudioException($"Table '{requestedTable}' does not exist in this compact database.");

    private static string RequireColumn(SqliteConnection connection, string table, string requestedColumn) =>
        SqliteRowService.ResolveColumnName(connection, null, table, requestedColumn)
        ?? throw new ContentStudioException($"Column '{requestedColumn}' does not exist in table '{table}'.");

    private void AddOwnedRows(IdRegistry registry, RecordDraftRequest request, RecordDefinition definition)
    {
        foreach (var child in request.Children)
        {
            EnsureRange(registry, child.Table);
            definition.Children.Add(new RecordChildClone
            {
                Table = child.Table,
                OwnerColumn = child.OwnerColumn,
                SourceId = child.SourceId,
                Id = Allocate(registry, request, definition.Key, child.Table, $"child:{child.Table}:{child.SourceId}"),
                Values = CleanChildValues(child)
            });
        }
    }

    private void AddOwnedSkillRows(IdRegistry registry, RecordDraftRequest request, RecordDefinition definition)
    {
        if (!request.Table.Equals("skills", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (request.Children.Count > 0)
        {
            foreach (var child in request.Children)
            {
                EnsureRange(registry, child.Table);
                definition.Children.Add(new RecordChildClone
                {
                    Table = child.Table,
                    OwnerColumn = child.OwnerColumn,
                    SourceId = child.SourceId,
                    Id = Allocate(registry, request, definition.Key, child.Table, $"child:{child.Table}:{child.SourceId}"),
                    Values = CleanChildValues(child)
                });
            }
            return;
        }

        using var connection = CompactConnectionFactory.OpenReadOnly(request.BaselinePath);
        var relationships = new[]
        {
            (Table: "skill_effects", Owner: "skill_id", Extra: string.Empty),
            (Table: "skill_reagents", Owner: "skill_id", Extra: string.Empty),
            (Table: "skill_products", Owner: "skill_id", Extra: string.Empty),
            (Table: "tagged_skills", Owner: "skill_id", Extra: string.Empty),
            (Table: "tooltip_skill_effects", Owner: "skill_id", Extra: string.Empty),
            (Table: "unit_reqs", Owner: "owner_id", Extra: " AND owner_type = 'Skill'")
        };
        foreach (var relationship in relationships)
        {
            if (!TableExists(connection, relationship.Table)) continue;
            EnsureRange(registry, relationship.Table);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id FROM {BaselineVerifier.QuoteIdentifier(relationship.Table)} WHERE {BaselineVerifier.QuoteIdentifier(relationship.Owner)} = @id{relationship.Extra} ORDER BY id;";
            command.Parameters.AddWithValue("@id", request.SourceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sourceId = Convert.ToUInt32(reader.GetInt64(0));
                definition.Children.Add(new RecordChildClone
                {
                    Table = relationship.Table,
                    OwnerColumn = relationship.Owner,
                    SourceId = sourceId,
                    Id = Allocate(registry, request, definition.Key, relationship.Table, $"child:{relationship.Table}:{sourceId}")
                });
            }
        }
    }

    private static Dictionary<string, string?> CleanChildValues(RecordChildDraft child)
    {
        var values = new Dictionary<string, string?>(child.Values, StringComparer.OrdinalIgnoreCase);
        values.Remove("id");
        values.Remove(child.OwnerColumn);
        return values;
    }

    private static Dictionary<string, string?> KeepChangedValues(
        SqliteConnection connection,
        string table,
        uint id,
        IReadOnlyDictionary<string, string?> values)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, requested) in values)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {BaselineVerifier.QuoteIdentifier(column)} FROM {BaselineVerifier.QuoteIdentifier(table)} WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            var current = command.ExecuteScalar();
            if (!Equivalent(current, requested)) result[column] = requested;
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> KeepChangedLocalizations(
        SqliteConnection connection,
        string table,
        uint id,
        IReadOnlyDictionary<string, Dictionary<string, string>> localizations)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, requestedLanguages) in localizations)
        {
            var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (language, requested) in requestedLanguages)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {BaselineVerifier.QuoteIdentifier(language)} FROM localized_texts WHERE tbl_name = @table COLLATE NOCASE AND tbl_column_name = @field COLLATE NOCASE AND idx = @id LIMIT 1;";
                command.Parameters.AddWithValue("@table", table);
                command.Parameters.AddWithValue("@field", field);
                command.Parameters.AddWithValue("@id", id);
                var current = command.ExecuteScalar();
                if (!LocalizationEquivalent(current, requested)) changed[language] = requested;
            }
            if (changed.Count > 0) result[field] = changed;
        }
        return result;
    }

    private static bool LocalizationEquivalent(object? current, string requested)
    {
        if (CatalogRecordService.IsCompactNull(current) && string.IsNullOrWhiteSpace(requested))
            return true;
        return Equivalent(current, requested);
    }

    private static bool Equivalent(object? current, string? requested)
    {
        if (CatalogRecordService.IsCompactNull(current) || CatalogRecordService.IsCompactNull(requested))
            return CatalogRecordService.IsCompactNull(current) && CatalogRecordService.IsCompactNull(requested);
        var currentText = Convert.ToString(current, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var requestedText = requested?.Trim() ?? string.Empty;
        if (currentText.Equals("t", StringComparison.OrdinalIgnoreCase) || currentText.Equals("f", StringComparison.OrdinalIgnoreCase))
        {
            var currentBoolean = currentText.Equals("t", StringComparison.OrdinalIgnoreCase);
            if (requestedText is "1" or "0") return currentBoolean == (requestedText == "1");
            if (bool.TryParse(requestedText, out var requestedBoolean)) return currentBoolean == requestedBoolean;
        }
        if (decimal.TryParse(currentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var currentNumber) &&
            decimal.TryParse(requestedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var requestedNumber))
            return currentNumber == requestedNumber;
        return currentText.Equals(requestedText, StringComparison.Ordinal);
    }

    private uint Allocate(IdRegistry registry, RecordDraftRequest request, string key, string table, string suffix) =>
        _ids.Allocate(registry, request.BaselinePath, table, $"{key}:{suffix}").Id;

    private static void EnsureRange(IdRegistry registry, string table)
    {
        IdRegistryService.NormalizeComparers(registry);
        IdRegistryService.CanonicalizeTableKeys(registry, table);
        if (!registry.Ranges.ContainsKey(table))
        {
            registry.Ranges[table] = new IdRange { Start = 8_000_000, End = 8_999_999 };
        }
    }

    private static void RetireLinkedAllocation(IdRegistry registry, string definitionKey, RecordLinkedClone linked)
    {
        IdRegistryService.NormalizeComparers(registry);
        IdRegistryService.CanonicalizeTableKeys(registry, linked.Table);
        var expectedKey = $"{definitionKey}:linked:{linked.Table}:{linked.SourceId}:{linked.LinkTable}:{linked.LinkSourceId}";
        var allocationKey = expectedKey;
        if (registry.Allocations.TryGetValue(linked.Table, out var allocations))
        {
            var matches = allocations.Where(pair => pair.Value == linked.Id).ToList();
            if (matches.Count > 1 || matches.Any(match => !IsOwned(match.Key, definitionKey)))
            {
                throw new ContentStudioException($"The ID registry does not uniquely own {linked.Table} ID {linked.Id} for '{definitionKey}'.");
            }
            if (matches.Count == 1)
            {
                allocationKey = matches[0].Key;
                allocations.Remove(allocationKey);
            }
        }

        IdRegistryService.AddTombstone(registry, linked.Table, allocationKey, linked.Id);
    }

    private static bool IsOwned(string allocationKey, string definitionKey) =>
        allocationKey.Equals(definitionKey, StringComparison.OrdinalIgnoreCase) ||
        allocationKey.StartsWith(definitionKey + ":", StringComparison.OrdinalIgnoreCase);

    private static bool LocalizationExists(SqliteConnection connection, string table, uint id, string field)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM localized_texts WHERE tbl_name = @table COLLATE NOCASE AND tbl_column_name = @field COLLATE NOCASE AND idx = @id);";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@field", field);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private void ApplyMutations(IReadOnlyList<FileMutation> mutations)
    {
        var stagedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var mutation in mutations.Where(value => value.Replacement is not null))
            {
                var directory = Path.GetDirectoryName(mutation.Path)
                    ?? throw new ContentStudioException($"Unable to determine the project directory for {mutation.Path}.");
                var stagingPath = Path.Combine(directory, $".{Path.GetFileName(mutation.Path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(stagingPath, mutation.Replacement!, new UTF8Encoding(false));
                stagedPaths[mutation.Path] = stagingPath;
            }

            foreach (var mutation in mutations)
            {
                EnsureUnchanged(mutation);
            }

            var applied = new List<FileMutation>();
            try
            {
                for (var index = 0; index < mutations.Count; index++)
                {
                    var mutation = mutations[index];
                    _beforeApply?.Invoke(index, mutation.Path);
                    EnsureUnchanged(mutation);
                    if (mutation.Replacement is null)
                    {
                        File.Delete(mutation.Path);
                    }
                    else
                    {
                        File.Move(stagedPaths[mutation.Path], mutation.Path, true);
                    }
                    applied.Add(mutation);
                }
            }
            catch (Exception exception)
            {
                if (applied.Count == 0 && exception is ContentStudioException)
                {
                    throw;
                }
                Exception? rollbackFailure = null;
                foreach (var mutation in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        EnsureStillApplied(mutation);
                        if (mutation.Original is null)
                        {
                            File.Delete(mutation.Path);
                        }
                        else
                        {
                            AtomicFile.WriteAllText(mutation.Path, mutation.Original);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackFailure ??= rollbackException;
                    }
                }

                throw rollbackFailure is null
                    ? new ContentStudioException("The entry could not be saved. All project files were restored.", exception)
                    : new ContentStudioException("The entry save failed and at least one project file could not be restored. Stop editing and recover the project from version control.", new AggregateException(exception, rollbackFailure));
            }
        }
        finally
        {
            foreach (var stagingPath in stagedPaths.Values)
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
            }
        }
    }

    private static void EnsureUnchanged(FileMutation mutation)
    {
        if (mutation.Original is null)
        {
            if (File.Exists(mutation.Path))
            {
                throw new ContentStudioException("A saved change was created while this entry was being prepared. Reload My changes before saving.");
            }
            return;
        }
        if (!File.Exists(mutation.Path) || !File.ReadAllText(mutation.Path).Equals(mutation.Original, StringComparison.Ordinal))
        {
            throw new ContentStudioException("This saved change or its ID registry was updated outside this editor. Reload it before saving your changes.");
        }
    }

    private static void EnsureStillApplied(FileMutation mutation)
    {
        if (mutation.Replacement is null)
        {
            if (File.Exists(mutation.Path))
                throw new ContentStudioException($"Cannot safely restore '{mutation.Path}' because another process recreated it after this save removed it.");
            return;
        }
        if (!File.Exists(mutation.Path) || !File.ReadAllText(mutation.Path).Equals(mutation.Replacement, StringComparison.Ordinal))
        {
            throw new ContentStudioException($"Cannot safely restore '{mutation.Path}' because another process changed it after this save wrote it.");
        }
    }

    private static string Normalize(string json) => json.TrimEnd() + Environment.NewLine;

    private static string FindAvailableKey(string projectDirectory, string proposed)
    {
        var key = proposed;
        var suffix = 2;
        while (File.Exists(Path.Combine(projectDirectory, "records", key + ".json")))
        {
            key = $"{proposed}-{suffix++}";
        }
        return key;
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        var previousDash = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousDash = false;
            }
            else if (!previousDash && builder.Length > 0)
            {
                builder.Append('-');
                previousDash = true;
            }
        }
        return builder.ToString().Trim('-');
    }

    private sealed record FileMutation(string Path, string? Original, string? Replacement);
}
