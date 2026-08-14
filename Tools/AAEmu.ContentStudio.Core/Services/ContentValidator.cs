using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class ContentValidator
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ko", "en_us", "zh_cn", "ja", "ru", "zh_tw", "de", "fr"
    };

    public ValidationReport ValidateProject(LoadedContentProject project, string compactPath)
    {
        var report = new ValidationReport();
        ValidateUnique(project.Recipes.Select(recipe => (recipe.Key, recipe.Id)), "recipe", report);
        ValidateUnique(project.Workbenches.Select(workbench => (workbench.Key, workbench.Id)), "workbench", report);
        ValidateUnique(project.Records.Select(record => (record.Key, record.Id)), "entry", report);
        ValidateUnique(project.Assertions.Select(assertion => (assertion.Key, 0u)), "assertion", report);

        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        foreach (var recipe in project.Recipes)
        {
            ValidateRecipe(connection, recipe, report);
        }

        foreach (var workbench in project.Workbenches)
        {
            ValidateWorkbench(connection, workbench, report);
        }

        foreach (var record in project.Records)
        {
            ValidateRecord(connection, record, report);
        }

        foreach (var assertion in project.Assertions)
        {
            ValidateAssertion(connection, assertion, report);
        }

        ValidateAllocatedIds(project, report);
        if (report.IsValid)
        {
            report.AddInformation("project.valid", $"Project '{project.Definition.Key}' passed source validation.");
        }

        return report;
    }

    public ValidationReport ValidateBuiltDatabase(string compactPath, LoadedContentProject project)
    {
        var report = new ValidationReport();
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        AddIntegrityResult(connection, report);

        foreach (var recipe in project.Recipes)
        {
            RequireRow(connection, "crafts", recipe.Id, report, recipe.Key);
            foreach (var material in recipe.Materials)
            {
                RequireReference(connection, "craft_materials", material.Id, "items", material.ItemId, report, recipe.Key);
            }
            foreach (var product in recipe.Products)
            {
                RequireReference(connection, "craft_products", product.Id, "items", product.ItemId, report, recipe.Key);
            }
            foreach (var packId in recipe.CraftPackIds)
            {
                RequireRow(connection, "craft_packs", packId, report, recipe.Key);
                RequireRelationshipCount(connection, "SELECT COUNT(*) FROM craft_pack_crafts WHERE craft_pack_id = @left AND craft_id = @right;", packId, recipe.Id, "recipe craft-pack link", report, recipe.Key);
            }
            if (recipe.Names.Count > 0)
            {
                RequireRelationshipCount(connection, "SELECT COUNT(*) FROM localized_texts WHERE tbl_name = 'crafts' AND tbl_column_name = 'title' AND idx = @right;", 0, recipe.Id, "recipe title localization", report, recipe.Key);
            }
        }

        foreach (var workbench in project.Workbenches)
        {
            RequireRow(connection, "doodad_almighties", workbench.Id, report, workbench.Key);
            RequireRow(connection, "craft_packs", workbench.CraftPack.Id, report, workbench.Key);
            foreach (var recipeId in workbench.RecipeIds)
            {
                RequireRow(connection, "crafts", recipeId, report, workbench.Key);
                RequireRelationshipCount(connection, "SELECT COUNT(*) FROM craft_pack_crafts WHERE craft_pack_id = @left AND craft_id = @right;", workbench.CraftPack.Id, recipeId, "workbench craft-pack link", report, workbench.Key);
            }
            if (workbench.Names.Count > 0)
            {
                RequireRelationshipCount(connection, "SELECT COUNT(*) FROM localized_texts WHERE tbl_name = 'doodad_almighties' AND tbl_column_name = 'name' AND idx = @right;", 0, workbench.Id, "workbench name localization", report, workbench.Key);
            }
        }

        foreach (var record in project.Records)
        {
            RequireRow(connection, record.Table, record.Id, report, record.Key);
            RequireValues(connection, record.Table, record.Id, record.Values, report, record.Key);
            foreach (var child in record.Children)
            {
                RequireRow(connection, child.Table, child.Id, report, record.Key);
                RequireValues(connection, child.Table, child.Id, child.Values, report, record.Key);
            }
            foreach (var linked in record.LinkedClones)
            {
                RequireRow(connection, linked.Table, linked.Id, report, record.Key);
                RequireValues(connection, linked.Table, linked.Id, linked.Values, report, record.Key);
            }
        }

        foreach (var assertion in project.Assertions)
        {
            RequireAssertion(connection, assertion, report);
        }

        AddDuplicateChecks(connection, report, project);
        if (report.IsValid)
        {
            report.AddInformation("artifact.valid", "The compiled database passed integrity and graph validation.", compactPath);
        }

        return report;
    }

    private static void ValidateRecipe(SqliteConnection connection, RecipeDefinition recipe, ValidationReport report)
    {
        var entity = recipe.Key.Length == 0 ? recipe.Id.ToString() : recipe.Key;
        if (recipe.Id == 0 || string.IsNullOrWhiteSpace(recipe.Key))
        {
            report.AddError("recipe.identity", "Recipe key and ID are required.", entity: entity);
        }
        if (!recipe.Key.StartsWith("recipe.", StringComparison.Ordinal) || !IsValidKey(recipe.Key))
        {
            report.AddError("recipe.key", "Recipe keys must use 'recipe.' followed by lowercase letters, numbers, dots, underscores, or hyphens.", entity: entity);
        }
        ValidateLanguages(recipe.Names.Keys.Concat(recipe.Descriptions.Keys), report, entity);
        if (recipe.Names.Count == 0 || recipe.Names.Values.All(string.IsNullOrWhiteSpace))
        {
            report.AddError("recipe.name", "At least one localized recipe name is required.", entity: entity);
        }
        if (recipe.Materials.Count == 0 || recipe.Products.Count == 0)
        {
            report.AddError("recipe.inputsOutputs", "A recipe needs at least one material and one product.", entity: entity);
        }
        if (recipe.Materials.Any(material => material.Id == 0 || material.ItemId == 0 || material.Amount <= 0))
        {
            report.AddError("recipe.material", "Every material needs a row ID, item ID, and positive amount.", entity: entity);
        }
        if (recipe.Products.Any(product => product.Id == 0 || product.ItemId == 0 || product.Amount <= 0 || product.Rate is < 0 or > 100))
        {
            report.AddError("recipe.product", "Every product needs valid IDs, positive amount, and a rate from 0 to 100.", entity: entity);
        }

        foreach (var itemId in recipe.Materials.Select(material => material.ItemId).Concat(recipe.Products.Select(product => product.ItemId)).Distinct())
        {
            if (!SqliteRowService.Exists(connection, null, "items", itemId))
            {
                report.AddError("recipe.itemMissing", $"Item {itemId} does not exist in the baseline.", entity: entity);
            }
        }

        if (recipe.SkillClone is not null)
        {
            if (!SqliteRowService.Exists(connection, null, "skills", recipe.SkillClone.SourceId))
            {
                report.AddError("recipe.skillSource", $"Source skill {recipe.SkillClone.SourceId} does not exist.", entity: entity);
            }
            var sourceEffects = SqliteRowService.ReadIds(connection, null, "SELECT id FROM skill_effects WHERE skill_id = @id ORDER BY id;", "@id", recipe.SkillClone.SourceId);
            if (sourceEffects.Count != recipe.SkillClone.SkillEffectRowIds.Length)
            {
                report.AddError("recipe.skillEffects", $"Source skill has {sourceEffects.Count} effect rows but {recipe.SkillClone.SkillEffectRowIds.Length} target IDs were supplied.", entity: entity);
            }
        }
        else if (!SqliteRowService.Exists(connection, null, "skills", recipe.SkillId))
        {
            report.AddError("recipe.skillMissing", $"Skill {recipe.SkillId} does not exist in the baseline.", entity: entity);
        }

        var requiredLocalizationRows = (recipe.Names.Count > 0 ? 1 : 0) + (recipe.Descriptions.Count > 0 ? 1 : 0);
        if (recipe.RowIds.Localization.Count < requiredLocalizationRows)
        {
            report.AddError("recipe.localizationIds", "Each localized title/description row needs an allocated localized_texts ID.", entity: entity);
        }
        if (recipe.CraftPackIds.Length != recipe.RowIds.CraftPackLinks.Length)
        {
            report.AddError("recipe.packLinkIds", "Each craft-pack link needs an allocated craft_pack_crafts ID.", entity: entity);
        }
    }

    private static void ValidateWorkbench(SqliteConnection connection, WorkbenchDefinition workbench, ValidationReport report)
    {
        var entity = workbench.Key.Length == 0 ? workbench.Id.ToString() : workbench.Key;
        if (workbench.Id == 0 || workbench.CraftPack.Id == 0 || string.IsNullOrWhiteSpace(workbench.Key))
        {
            report.AddError("workbench.identity", "Workbench key, doodad ID, and craft-pack ID are required.", entity: entity);
        }
        if (!workbench.Key.StartsWith("workbench.", StringComparison.Ordinal) || !IsValidKey(workbench.Key))
        {
            report.AddError("workbench.key", "Workbench keys must use 'workbench.' followed by lowercase letters, numbers, dots, underscores, or hyphens.", entity: entity);
        }
        ValidateLanguages(workbench.Names.Keys, report, entity);
        if (!SqliteRowService.Exists(connection, null, "doodad_almighties", workbench.SourceDoodadId))
        {
            report.AddError("workbench.source", $"Source doodad {workbench.SourceDoodadId} does not exist.", entity: entity);
            return;
        }

        var groups = SqliteRowService.ReadIds(connection, null, "SELECT id FROM doodad_func_groups WHERE doodad_almighty_id = @id ORDER BY id;", "@id", workbench.SourceDoodadId);
        RequireMapCoverage(groups, workbench.RowIds.FunctionGroups, "function groups", report, entity);
        foreach (var groupId in groups)
        {
            var functions = SqliteRowService.ReadIds(connection, null, "SELECT id FROM doodad_funcs WHERE doodad_func_group_id = @id ORDER BY id;", "@id", groupId);
            RequireMapCoverage(functions, workbench.RowIds.Functions, "functions", report, entity);
            var phases = SqliteRowService.ReadIds(connection, null, "SELECT id FROM doodad_phase_funcs WHERE doodad_func_group_id = @id ORDER BY id;", "@id", groupId);
            RequireMapCoverage(phases, workbench.RowIds.PhaseFunctions, "phase functions", report, entity);
        }

        var payloads = ReadCraftPackPayloadIds(connection, groups);
        RequireMapCoverage(payloads, workbench.RowIds.CraftPackPayloads, "craft-pack payloads", report, entity);
        if (workbench.Names.Count > 0 && workbench.RowIds.Localization.Count == 0)
        {
            report.AddError("workbench.localizationIds", "Each localized workbench name needs an allocated localized_texts ID.", entity: entity);
        }
        if (workbench.RecipeIds.Length != workbench.RowIds.CraftPackLinks.Length)
        {
            report.AddError("workbench.packLinkIds", "Each workbench recipe link needs an allocated craft_pack_crafts ID.", entity: entity);
        }
    }

    private static void ValidateRecord(SqliteConnection connection, RecordDefinition record, ValidationReport report)
    {
        var entity = string.IsNullOrWhiteSpace(record.Key) ? $"{record.Table}/{record.Id}" : record.Key;
        if (!record.Key.StartsWith("record.", StringComparison.Ordinal) || !IsValidKey(record.Key))
            report.AddError("record.key", "Changed-entry keys must begin with 'record.' and contain only lowercase letters, numbers, dots, underscores, or hyphens.", entity: entity);
        if (string.IsNullOrWhiteSpace(record.Table))
            report.AddError("record.identity", "A changed entry needs a source table and output identity.", entity: entity);
        else if (!SqliteRowService.Exists(connection, null, record.Table, record.SourceId))
            report.AddError("record.source", $"Source {record.Table} entry {record.SourceId} does not exist.", entity: entity);
        if (record.Mode == RecordChangeMode.Duplicate && record.Id == 0)
            report.AddError("record.duplicateId", "A copied entry needs a non-zero custom output ID.", entity: entity);
        if (record.Mode == RecordChangeMode.Modify && record.Id != record.SourceId)
            report.AddError("record.modifyId", "A modification must retain the source entry ID.", entity: entity);
        foreach (var linked in record.LinkedClones)
        {
            if (linked.Id == 0 || string.IsNullOrWhiteSpace(linked.Table) || string.IsNullOrWhiteSpace(linked.LinkTable) || string.IsNullOrWhiteSpace(linked.LinkColumn))
                report.AddError("record.linkedIdentity", "A private linked row needs a table, output ID, and target link.", entity: entity);
            if (linked.SourceId > 0 && !SqliteRowService.Exists(connection, null, linked.Table, linked.SourceId))
                report.AddError("record.linkedSource", $"Source {linked.Table} entry {linked.SourceId} does not exist.", entity: entity);
            if (!record.Children.Any(child => child.Table.Equals(linked.LinkTable, StringComparison.OrdinalIgnoreCase) && child.SourceId == linked.LinkSourceId) &&
                !(record.Table.Equals(linked.LinkTable, StringComparison.OrdinalIgnoreCase) && record.SourceId == linked.LinkSourceId))
                report.AddError("record.linkTarget", $"The private {linked.Table} row cannot find its {linked.LinkTable} link target.", entity: entity);
        }
        ValidateLanguages(record.Localizations.Values.SelectMany(values => values.Keys), report, entity);
    }

    private static void ValidateAssertion(SqliteConnection connection, ContentAssertionDefinition assertion, ValidationReport report)
    {
        var entity = string.IsNullOrWhiteSpace(assertion.Key) ? "assertion" : assertion.Key;
        if (!assertion.Key.StartsWith("assertion.", StringComparison.Ordinal) || !IsValidKey(assertion.Key))
            report.AddError("assertion.key", "Assertion keys must begin with 'assertion.' and contain only lowercase letters, numbers, dots, underscores, or hyphens.", entity: entity);
        if (string.IsNullOrWhiteSpace(assertion.Description))
            report.AddError("assertion.description", "An assertion needs a human-readable description.", entity: entity);
        if (!IsReadOnlyScalarQuery(assertion.Query))
        {
            report.AddError("assertion.query", "Assertions must contain one read-only SELECT or WITH query.", entity: entity);
            return;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = assertion.Query;
            _ = command.ExecuteScalar();
        }
        catch (SqliteException exception)
        {
            report.AddError("assertion.query", $"Assertion query is not valid for this baseline: {exception.Message}", entity: entity);
        }
    }

    private static bool IsReadOnlyScalarQuery(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.EndsWith(';')) trimmed = trimmed[..^1].TrimEnd();
        if (trimmed.Contains(';')) return false;
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            return false;
        return !Regex.IsMatch(trimmed, @"\b(ATTACH|ALTER|CREATE|DELETE|DETACH|DROP|INSERT|PRAGMA|REINDEX|REPLACE|UPDATE|VACUUM)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static List<uint> ReadCraftPackPayloadIds(SqliteConnection connection, IEnumerable<uint> groups)
    {
        var ids = new List<uint>();
        foreach (var group in groups)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT actual_func_id FROM doodad_funcs WHERE doodad_func_group_id = @id AND actual_func_type = 'DoodadFuncCraftPack' ORDER BY id;";
            command.Parameters.AddWithValue("@id", group);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(Convert.ToUInt32(reader.GetInt64(0)));
            }
        }
        return ids;
    }

    private static void RequireMapCoverage(IEnumerable<uint> sourceIds, Dictionary<uint, uint> mapping, string label, ValidationReport report, string entity)
    {
        foreach (var id in sourceIds)
        {
            if (!mapping.TryGetValue(id, out var targetId) || targetId == 0)
            {
                report.AddError("workbench.cloneMap", $"Source {label} row {id} does not have a target ID.", entity: entity);
            }
        }
    }

    private static void ValidateUnique(IEnumerable<(string Key, uint Id)> entities, string type, ValidationReport report)
    {
        foreach (var duplicate in entities.GroupBy(entity => entity.Key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            report.AddError("project.duplicateKey", $"Duplicate {type} key '{duplicate.Key}'.");
        }
        foreach (var duplicate in entities.GroupBy(entity => entity.Id).Where(group => group.Key != 0 && group.Count() > 1))
        {
            report.AddError("project.duplicateId", $"Duplicate {type} ID {duplicate.Key}.");
        }
    }

    private static void ValidateAllocatedIds(LoadedContentProject project, ValidationReport report)
    {
        var declared = project.Registry.Allocations.SelectMany(table => table.Value.Select(value => (Table: table.Key, Key: value.Key, Id: value.Value))).ToList();
        foreach (var duplicate in declared.GroupBy(value => (value.Table, value.Id)).Where(group => group.Count() > 1))
        {
            report.AddError("registry.duplicate", $"ID {duplicate.Key.Id} is allocated more than once for table '{duplicate.Key.Table}'.");
        }
        foreach (var allocation in declared)
        {
            if (!project.Registry.Ranges.TryGetValue(allocation.Table, out var range) || allocation.Id < range.Start || allocation.Id > range.End)
            {
                report.AddError("registry.range", $"Allocation {allocation.Table}/{allocation.Key}={allocation.Id} is outside its registered range.");
            }
        }

        var expected = new List<(string Table, uint Id, string Entity)>();
        foreach (var recipe in project.Recipes)
        {
            expected.Add(("crafts", recipe.Id, recipe.Key));
            expected.AddRange(recipe.Materials.Select(row => ("craft_materials", row.Id, recipe.Key)));
            expected.AddRange(recipe.Products.Select(row => ("craft_products", row.Id, recipe.Key)));
            expected.AddRange(recipe.RowIds.Localization.Values.Select(id => ("localized_texts", id, recipe.Key)));
            expected.AddRange(recipe.RowIds.CraftPackLinks.Select(id => ("craft_pack_crafts", id, recipe.Key)));
            if (recipe.SkillClone is not null)
            {
                expected.Add(("skills", recipe.SkillClone.Id, recipe.Key));
                expected.AddRange(recipe.SkillClone.SkillEffectRowIds.Select(id => ("skill_effects", id, recipe.Key)));
            }
        }
        foreach (var workbench in project.Workbenches)
        {
            expected.Add(("doodad_almighties", workbench.Id, workbench.Key));
            expected.Add(("craft_packs", workbench.CraftPack.Id, workbench.Key));
            expected.AddRange(workbench.RowIds.FunctionGroups.Values.Select(id => ("doodad_func_groups", id, workbench.Key)));
            expected.AddRange(workbench.RowIds.Functions.Values.Select(id => ("doodad_funcs", id, workbench.Key)));
            expected.AddRange(workbench.RowIds.PhaseFunctions.Values.Select(id => ("doodad_phase_funcs", id, workbench.Key)));
            expected.AddRange(workbench.RowIds.CraftPackPayloads.Values.Select(id => ("doodad_func_craft_packs", id, workbench.Key)));
            expected.AddRange(workbench.RowIds.Localization.Values.Select(id => ("localized_texts", id, workbench.Key)));
            expected.AddRange(workbench.RowIds.CraftPackLinks.Select(id => ("craft_pack_crafts", id, workbench.Key)));
        }
        foreach (var record in project.Records.Where(record => record.Mode == RecordChangeMode.Duplicate))
        {
            expected.Add((record.Table, record.Id, record.Key));
            expected.AddRange(record.LocalizationRowIds.Values.Select(id => ("localized_texts", id, record.Key)));
            expected.AddRange(record.Children.Select(child => (child.Table, child.Id, record.Key)));
        }
        foreach (var record in project.Records)
        {
            expected.AddRange(record.LinkedClones.Select(linked => (linked.Table, linked.Id, record.Key)));
        }
        foreach (var duplicate in expected.GroupBy(row => (row.Table, row.Id)).Where(group => group.Key.Id != 0 && group.Count() > 1))
        {
            report.AddError("project.rowIdCollision", $"Target ID {duplicate.Key.Id} is used more than once in table '{duplicate.Key.Table}'.");
        }
        foreach (var row in expected.Where(row => row.Id != 0))
        {
            if (!project.Registry.Allocations.TryGetValue(row.Table, out var allocations) || !allocations.Values.Contains(row.Id))
            {
                report.AddError("registry.unownedId", $"{row.Table} ID {row.Id} used by '{row.Entity}' is not owned by the ID registry.", entity: row.Entity);
            }
        }
    }

    private static void ValidateLanguages(IEnumerable<string> languages, ValidationReport report, string entity)
    {
        foreach (var language in languages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!SupportedLanguages.Contains(language))
            {
                report.AddError("localization.language", $"Unsupported compact language column '{language}'.", entity: entity);
            }
        }
    }

    private static bool IsValidKey(string key)
    {
        return key.All(character => char.IsLower(character) || char.IsDigit(character) || character is '.' or '_' or '-');
    }

    private static void AddIntegrityResult(SqliteConnection connection, ValidationReport report)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var value = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        if (!value.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            report.AddError("artifact.integrity", value);
        }
    }

    private static void AddDuplicateChecks(SqliteConnection connection, ValidationReport report, LoadedContentProject project)
    {
        var tables = new[] { "crafts", "skills", "skill_effects", "craft_materials", "craft_products", "craft_packs", "craft_pack_crafts", "doodad_almighties", "doodad_func_groups", "doodad_funcs", "doodad_phase_funcs", "doodad_func_craft_packs", "localized_texts" }
            .Concat(project.Records.Where(record => record.Mode == RecordChangeMode.Duplicate).Select(record => record.Table))
            .Concat(project.Records.SelectMany(record => record.Children).Select(child => child.Table))
            .Concat(project.Records.SelectMany(record => record.LinkedClones).Select(linked => linked.Table))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id FROM {BaselineVerifier.QuoteIdentifier(table)} GROUP BY id HAVING COUNT(*) > 1 LIMIT 1;";
            var value = command.ExecuteScalar();
            if (value is not null)
            {
                report.AddError("artifact.duplicateId", $"Table '{table}' contains duplicate ID {value}.", entity: table);
            }
        }

    }

    private static void RequireRelationshipCount(SqliteConnection connection, string sql, uint left, uint right, string label, ValidationReport report, string entity)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@left", left);
        command.Parameters.AddWithValue("@right", right);
        var count = Convert.ToInt32(command.ExecuteScalar());
        if (count != 1)
        {
            report.AddError("artifact.relationshipCount", $"Expected one {label}, but found {count}.", entity: entity);
        }
    }

    private static void RequireRow(SqliteConnection connection, string table, uint id, ValidationReport report, string entity)
    {
        if (!SqliteRowService.Exists(connection, null, table, id))
        {
            report.AddError("artifact.rowMissing", $"Expected {table} row {id} is missing.", entity: entity);
        }
    }

    private static void RequireValues(SqliteConnection connection, string table, uint id, IReadOnlyDictionary<string, string?> values, ValidationReport report, string entity)
    {
        foreach (var (column, expected) in values)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {BaselineVerifier.QuoteIdentifier(column)} FROM {BaselineVerifier.QuoteIdentifier(table)} WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            object? actual;
            try
            {
                actual = command.ExecuteScalar();
            }
            catch (SqliteException exception)
            {
                report.AddError("artifact.valueColumn", $"Could not verify {table} {id}.{column}: {exception.Message}", entity: entity);
                continue;
            }

            if (!ValuesEqual(expected, actual))
            {
                report.AddError("artifact.valueMismatch", $"Expected {table} {id}.{column} to be '{FormatValue(expected)}', but the artifact contains '{FormatValue(actual)}'.", entity: entity);
            }
        }
    }

    private static void RequireAssertion(SqliteConnection connection, ContentAssertionDefinition assertion, ValidationReport report)
    {
        using var command = connection.CreateCommand();
        command.CommandText = assertion.Query;
        var actual = command.ExecuteScalar();
        if (!ValuesEqual(assertion.Expected, actual))
        {
            report.AddError("artifact.assertion", $"{assertion.Description} Expected '{assertion.Expected}', but the query returned '{FormatValue(actual)}'.", entity: assertion.Key);
        }
        else
        {
            report.AddInformation("artifact.assertion", assertion.Description, entity: assertion.Key);
        }
    }

    private static bool ValuesEqual(object? expected, object? actual)
    {
        if (CatalogRecordService.IsCompactNull(expected) || CatalogRecordService.IsCompactNull(actual))
            return CatalogRecordService.IsCompactNull(expected) && CatalogRecordService.IsCompactNull(actual);

        var expectedText = Convert.ToString(expected, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var actualText = Convert.ToString(actual, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (TryBoolean(expectedText, out var expectedBoolean) && TryBoolean(actualText, out var actualBoolean))
            return expectedBoolean == actualBoolean;
        if (decimal.TryParse(expectedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber) &&
            decimal.TryParse(actualText, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNumber))
            return expectedNumber == actualNumber;
        return expectedText.Equals(actualText, StringComparison.Ordinal);
    }

    private static bool TryBoolean(string value, out bool result)
    {
        if (value.Equals("t", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            result = true;
            return true;
        }
        if (value.Equals("f", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }

    private static string FormatValue(object? value) => CatalogRecordService.IsCompactNull(value)
        ? "null"
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static void RequireReference(SqliteConnection connection, string rowTable, uint rowId, string referenceTable, uint referenceId, ValidationReport report, string entity)
    {
        RequireRow(connection, rowTable, rowId, report, entity);
        RequireRow(connection, referenceTable, referenceId, report, entity);
    }
}
