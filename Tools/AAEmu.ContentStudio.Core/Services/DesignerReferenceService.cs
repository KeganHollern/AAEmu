using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

/// <summary>
/// Translates database relationships into names suitable for the designer UI.
/// Numeric values remain the storage format, but are never required to identify
/// an entry in the Content Studio interface.
/// </summary>
public sealed class DesignerReferenceService
{
    public DesignerReferenceOption? Resolve(string compactPath, string projectPath, string table, uint value)
    {
        if (value == 0)
        {
            return null;
        }

        if (table.Equals("abilities", StringComparison.OrdinalIgnoreCase) && CatalogRecordService.Abilities.TryGetValue(value, out var ability))
        {
            return new DesignerReferenceOption(value, ability.Name, ability.Description, table);
        }

        var custom = ReadCustomRecords(projectPath, table).FirstOrDefault(option => option.Value == value);
        if (custom is not null)
        {
            return custom;
        }

        var record = new CatalogRecordService().GetRecord(compactPath, table, value);
        if (record is null)
        {
            return new DesignerReferenceOption(value, $"Unavailable {FriendlyType(table)}", "The linked entry is not present in this baseline.", table);
        }

        return new DesignerReferenceOption(value, FriendlyRecordName(record), Describe(record), table);
    }

    public IReadOnlyList<DesignerReferenceOption> Search(string compactPath, string projectPath, string table, string query, int limit = 24)
    {
        var search = query.Trim();
        if (search.Length == 0)
        {
            return [];
        }

        if (table.Equals("abilities", StringComparison.OrdinalIgnoreCase))
        {
            return CatalogRecordService.Abilities
                .Where(pair => pair.Value.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || pair.Value.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(pair => new DesignerReferenceOption(pair.Key, pair.Value.Name, pair.Value.Description, table))
                .Take(limit)
                .ToList();
        }

        var matches = new List<DesignerReferenceOption>();
        matches.AddRange(ReadCustomRecords(projectPath, table)
            .Where(option => option.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || option.Context.Contains(search, StringComparison.OrdinalIgnoreCase)));

        matches.AddRange(new CatalogSearchService().SearchEverything(compactPath, search, limit: 200).Results
            .Where(result => result.Table.Equals(table, StringComparison.OrdinalIgnoreCase))
            .Select(result => new DesignerReferenceOption(result.Id, result.Name, result.Context ?? result.KindLabel, table)));

        // Accepting a known value remains useful for imported plans, but the UI
        // immediately resolves it to a name and never displays the number.
        if (uint.TryParse(search, out var exactValue))
        {
            var exact = Resolve(compactPath, projectPath, table, exactValue);
            if (exact is not null)
            {
                matches.Add(exact);
            }
        }

        var ordered = matches
            .GroupBy(option => option.Value)
            .Select(group => group.First())
            .OrderBy(option => option.Name.Equals(search, StringComparison.OrdinalIgnoreCase) ? 0 : option.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        AddFriendlyVariants(ordered);
        return ordered;
    }

    private static List<DesignerReferenceOption> ReadCustomRecords(string projectPath, string table)
    {
        try
        {
            return new ProjectRepository().LoadProject(projectPath).Records
                .Where(record => record.Table.Equals(table, StringComparison.OrdinalIgnoreCase))
                .Select(record => new DesignerReferenceOption(record.Id, record.DisplayName, "Saved in My changes", table, true))
                .ToList();
        }
        catch (ContentStudioException)
        {
            return [];
        }
    }

    private static string FriendlyRecordName(CatalogRecord record)
    {
        var generatedSuffix = $" {record.Id}";
        if (!record.Name.EndsWith(generatedSuffix, StringComparison.Ordinal) || record.Name.Length == generatedSuffix.Length)
        {
            return record.Name;
        }

        var meaningful = record.Fields.FirstOrDefault(field =>
            !field.IsIdentity && !field.IsNull && field.Name is "name" or "title" or "code" or "alias" or "model")?.Value;
        return string.IsNullOrWhiteSpace(meaningful) ? $"Unnamed {record.KindLabel.ToLowerInvariant()}" : meaningful;
    }

    private static string Describe(CatalogRecord record)
    {
        var friendlyContext = record.Table.ToLowerInvariant() switch
        {
            "doodad_almighties" => "Existing crafting object",
            "actability_categories" => "Crafting proficiency",
            "item_grades" => "Item quality",
            "equip_item_sets" => "Equipment set",
            "equip_item_attr_modifiers" => "Primary stat profile",
            _ => null
        };
        if (friendlyContext is not null) return friendlyContext;
        var facts = record.Fields
            .Where(field => field.IsEssential && !field.IsIdentity && !field.IsNull && field.Name is not "name" and not "title")
            .Take(2)
            .Select(field => $"{field.Label}: {field.Value}")
            .ToList();
        return facts.Count == 0 ? record.KindLabel : string.Join(" · ", facts);
    }

    private static string FriendlyType(string table) => CatalogRecordService.FriendlyTableName(table).ToLowerInvariant();

    private static void AddFriendlyVariants(List<DesignerReferenceOption> options)
    {
        foreach (var group in options.GroupBy(option => option.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var index = 1;
            foreach (var option in group.ToList())
            {
                var position = options.IndexOf(option);
                options[position] = option with { Context = $"{option.Context} · Variant {index++}" };
            }
        }
    }
}
