using System.Text;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class RecordScaffoldService
{
    private readonly ProjectRepository _repository = new();
    private readonly IdRegistryService _ids = new();

    public RecordDraftResult Save(RecordDraftRequest request)
    {
        if (request.SourceId == 0 || string.IsNullOrWhiteSpace(request.Table))
        {
            throw new ContentStudioException("Choose a valid entry before saving changes.");
        }
        if (request.Mode == RecordChangeMode.Duplicate && request.Table is "crafts" or "doodad_almighties")
        {
            throw new ContentStudioException(request.Table == "crafts"
                ? "Recipes have connected ingredients, products, and skills. Use Recipe maker so the complete recipe is copied."
                : "Workbenches have connected function graphs. Use Workbench maker so the complete workbench is copied.");
        }

        var project = _repository.LoadProject(request.ProjectPath);
        EnsureRange(project.Registry, request.Table);
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

        if (request.Mode == RecordChangeMode.Duplicate)
        {
            definition.Id = Allocate(project, request, key, request.Table, "row");
            foreach (var field in definition.Localizations.Keys)
            {
                definition.LocalizationRowIds[field] = Allocate(project, request, key, "localized_texts", $"localization:{field}");
            }
            if (request.Table.Equals("skills", StringComparison.OrdinalIgnoreCase))
            {
                AddOwnedSkillRows(project, request, definition);
            }
            else
            {
                AddOwnedRows(project, request, definition);
            }
        }
        else
        {
            using var connection = CompactConnectionFactory.OpenReadOnly(request.BaselinePath);
            foreach (var field in definition.Localizations.Keys.Where(field => !LocalizationExists(connection, request.Table, request.SourceId, field)))
            {
                definition.LocalizationRowIds[field] = Allocate(project, request, key, "localized_texts", $"localization:{field}");
            }
            definition.Children = request.Children.Select(child => new RecordChildClone
            {
                Table = child.Table,
                OwnerColumn = child.OwnerColumn,
                SourceId = child.SourceId,
                Id = child.SourceId,
                Values = CleanChildValues(child)
            }).ToList();
        }

        var directory = Path.Combine(project.ProjectDirectory, "records");
        var path = Path.Combine(directory, key + ".json");
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(path, ContentStudioJson.Serialize(definition) + Environment.NewLine);
        _repository.SaveRegistry(request.ProjectPath, project.Registry);
        return new RecordDraftResult { Key = key, Id = definition.Id, Path = path, RelatedRowsCopied = definition.Children.Count };
    }

    private void AddOwnedRows(LoadedContentProject project, RecordDraftRequest request, RecordDefinition definition)
    {
        foreach (var child in request.Children)
        {
            EnsureRange(project.Registry, child.Table);
            definition.Children.Add(new RecordChildClone
            {
                Table = child.Table,
                OwnerColumn = child.OwnerColumn,
                SourceId = child.SourceId,
                Id = Allocate(project, request, definition.Key, child.Table, $"child:{child.Table}:{child.SourceId}"),
                Values = CleanChildValues(child)
            });
        }
    }

    private void AddOwnedSkillRows(LoadedContentProject project, RecordDraftRequest request, RecordDefinition definition)
    {
        if (!request.Table.Equals("skills", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (request.Children.Count > 0)
        {
            foreach (var child in request.Children)
            {
                EnsureRange(project.Registry, child.Table);
                definition.Children.Add(new RecordChildClone
                {
                    Table = child.Table,
                    OwnerColumn = child.OwnerColumn,
                    SourceId = child.SourceId,
                    Id = Allocate(project, request, definition.Key, child.Table, $"child:{child.Table}:{child.SourceId}"),
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
            EnsureRange(project.Registry, relationship.Table);
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
                    Id = Allocate(project, request, definition.Key, relationship.Table, $"child:{relationship.Table}:{sourceId}")
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

    private uint Allocate(LoadedContentProject project, RecordDraftRequest request, string key, string table, string suffix) =>
        _ids.Allocate(project.Registry, request.BaselinePath, table, $"{key}:{suffix}").Id;

    private static void EnsureRange(IdRegistry registry, string table)
    {
        if (!registry.Ranges.ContainsKey(table))
        {
            registry.Ranges[table] = new IdRange { Start = 8_000_000, End = 8_999_999 };
        }
    }

    private static bool LocalizationExists(SqliteConnection connection, string table, uint id, string field)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM localized_texts WHERE tbl_name = @table AND tbl_column_name = @field AND idx = @id);";
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
}
