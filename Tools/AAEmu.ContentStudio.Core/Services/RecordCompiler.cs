using System.Globalization;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

internal sealed class RecordCompiler
{
    public IReadOnlyList<ContentChange> Compile(SqliteConnection connection, SqliteTransaction transaction, RecordDefinition definition)
    {
        var table = RequireTable(connection, transaction, definition.Table);
        var changedValues = definition.Mode == RecordChangeMode.Modify
            ? ReadChangedValues(connection, transaction, table, definition.Id, definition.Values)
            : [];
        var values = ConvertValues(connection, transaction, table, definition.Values);
        ApplyLinkedOverrides(definition, definition.Table, definition.SourceId, values);
        foreach (var linked in definition.LinkedClones)
        {
            var linkedTable = RequireTable(connection, transaction, linked.Table);
            var linkedValues = ConvertValues(connection, transaction, linkedTable, linked.Values);
            linkedValues["id"] = linked.Id;
            if (linked.SourceId == 0)
            {
                SqliteRowService.Insert(connection, transaction, linkedTable, linkedValues);
            }
            else
            {
                SqliteRowService.CloneById(connection, transaction, linkedTable, linked.SourceId, linkedValues);
            }
        }
        if (definition.Mode == RecordChangeMode.Duplicate)
        {
            values["id"] = definition.Id;
            SqliteRowService.CloneById(connection, transaction, table, definition.SourceId, values);
            foreach (var child in definition.Children)
            {
                var childTable = RequireTable(connection, transaction, child.Table);
                var ownerColumn = RequireColumn(connection, transaction, childTable, child.OwnerColumn);
                var childValues = ConvertValues(connection, transaction, childTable, child.Values);
                ApplyLinkedOverrides(definition, child.Table, child.SourceId, childValues);
                childValues["id"] = child.Id;
                childValues[ownerColumn] = definition.Id;
                SqliteRowService.CloneById(connection, transaction, childTable, child.SourceId, childValues);
            }
        }
        else
        {
            UpdateById(connection, transaction, table, definition.Id, values);
            foreach (var child in definition.Children)
            {
                var childTable = RequireTable(connection, transaction, child.Table);
                var childValues = ConvertValues(connection, transaction, childTable, child.Values);
                ApplyLinkedOverrides(definition, child.Table, child.SourceId, childValues);
                UpdateById(connection, transaction, childTable, child.Id, childValues);
            }
        }

        foreach (var (field, localizedValues) in definition.Localizations)
        {
            _ = BaselineVerifier.QuoteIdentifier(field);
            var localizationField = ResolveLocalizationField(connection, transaction, table, definition.SourceId, field);
            if (definition.Mode == RecordChangeMode.Modify && LocalizationExists(connection, transaction, table, definition.Id, localizationField))
            {
                LocalizationCompiler.Update(connection, transaction, table, localizationField, definition.Id, localizedValues);
            }
            else
            {
                var allocation = definition.LocalizationRowIds.FirstOrDefault(pair => pair.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (allocation.Value == 0)
                {
                    throw new ContentStudioException($"The translated field '{field}' is missing its reserved row ID.");
                }
                LocalizationCompiler.Insert(connection, transaction, allocation.Value, table, localizationField, definition.Id, localizedValues);
            }
        }

        var action = definition.Mode == RecordChangeMode.Duplicate ? "duplicate" : "modify";
        var summary = definition.Mode == RecordChangeMode.Duplicate
            ? $"Created a separate {CatalogRecordService.FriendlyTableName(definition.Table).ToLowerInvariant()}, including {definition.Children.Count} connected rows and {definition.LinkedClones.Count} private linked rows."
            : changedValues.Count == 0
                ? $"Verified {CatalogRecordService.FriendlyTableName(definition.Table).ToLowerInvariant()}; its saved values already matched the original."
                : $"Changed {CatalogRecordService.FriendlyTableName(definition.Table).ToLowerInvariant()}: {string.Join(", ", changedValues.Select(change => $"{CatalogRecordService.FriendlyName(change.Column)} {Format(change.Before)} → {Format(change.After)}"))}.";
        return [new ContentChange("record", definition.Key, definition.Id, action, summary)];
    }

    private static void ApplyLinkedOverrides(RecordDefinition definition, string table, uint sourceId, Dictionary<string, object?> values)
    {
        foreach (var linked in definition.LinkedClones.Where(linked => linked.LinkTable.Equals(table, StringComparison.OrdinalIgnoreCase) && linked.LinkSourceId == sourceId))
        {
            values[linked.LinkColumn] = linked.Id;
        }
    }

    private static Dictionary<string, object?> ConvertValues(SqliteConnection connection, SqliteTransaction transaction, string table, IReadOnlyDictionary<string, string?> values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({BaselineVerifier.QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) types[reader.GetString(1)] = reader.GetString(2);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            var canonicalName = types.Keys.FirstOrDefault(column => column.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (canonicalName is null || canonicalName.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            var type = types[canonicalName];
            if (CatalogRecordService.IsCompactNull(value))
            {
                result[canonicalName] = null;
            }
            else if (type.Contains("INT", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw new ContentStudioException($"'{CatalogRecordService.FriendlyName(name)}' must be a whole number.");
                result[canonicalName] = integer;
            }
            else if (type.Equals("NUM", StringComparison.OrdinalIgnoreCase))
            {
                if (CatalogRecordService.IsBooleanField(name, type, value))
                    result[canonicalName] = ParseBoolean(value) ? "t" : "f";
                else if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    result[canonicalName] = integer;
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    result[canonicalName] = number;
                else
                    throw new ContentStudioException($"'{CatalogRecordService.FriendlyName(name)}' must be a number or a Yes/No value.");
            }
            else if (type.Contains("REAL", StringComparison.OrdinalIgnoreCase) || type.Contains("FLOA", StringComparison.OrdinalIgnoreCase) || type.Contains("DOUB", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new ContentStudioException($"'{CatalogRecordService.FriendlyName(name)}' must be a number.");
                result[canonicalName] = number;
            }
            else
            {
                result[canonicalName] = value;
            }
        }
        return result;
    }

    private static bool ParseBoolean(string? value)
    {
        if (value is not null && (value.Equals("t", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"))
            return true;
        if (value is not null && (value.Equals("f", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0"))
            return false;
        throw new ContentStudioException($"'{value}' is not a valid Yes/No value.");
    }

    private static List<(string Column, object? Before, string? After)> ReadChangedValues(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        uint id,
        IReadOnlyDictionary<string, string?> values)
    {
        var result = new List<(string Column, object? Before, string? After)>();
        foreach (var (column, after) in values)
        {
            var canonicalColumn = RequireColumn(connection, transaction, table, column);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {BaselineVerifier.QuoteIdentifier(canonicalColumn)} FROM {BaselineVerifier.QuoteIdentifier(table)} WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            var before = command.ExecuteScalar();
            if (!Equivalent(before, after)) result.Add((canonicalColumn, before, after));
        }
        return result;
    }

    private static bool Equivalent(object? before, string? after)
    {
        if (CatalogRecordService.IsCompactNull(before) || CatalogRecordService.IsCompactNull(after))
            return CatalogRecordService.IsCompactNull(before) && CatalogRecordService.IsCompactNull(after);
        var beforeText = Convert.ToString(before, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var afterText = after?.Trim() ?? string.Empty;
        if ((beforeText is "t" or "f") && (afterText is "1" or "0" or "t" or "f"))
            return (beforeText == "t") == (afterText is "1" or "t");
        return beforeText.Equals(afterText, StringComparison.Ordinal);
    }

    private static string Format(object? value) => CatalogRecordService.IsCompactNull(value)
        ? "null"
        : $"'{Convert.ToString(value, CultureInfo.InvariantCulture)}'";

    private static void UpdateById(SqliteConnection connection, SqliteTransaction transaction, string table, uint id, Dictionary<string, object?> values)
    {
        if (values.Count == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var assignments = new List<string>();
        var index = 0;
        foreach (var (name, value) in values)
        {
            var parameter = $"@value{index++}";
            assignments.Add($"{BaselineVerifier.QuoteIdentifier(name)} = {parameter}");
            command.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        }
        command.Parameters.AddWithValue("@id", id);
        command.CommandText = $"UPDATE {BaselineVerifier.QuoteIdentifier(table)} SET {string.Join(", ", assignments)} WHERE id = @id;";
        if (command.ExecuteNonQuery() != 1)
            throw new ContentStudioException($"Could not change {table} row {id}; the source entry does not exist.");
    }

    private static string ResolveLocalizationField(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        uint id,
        string field)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT tbl_column_name FROM localized_texts WHERE tbl_name = @table COLLATE NOCASE AND tbl_column_name = @field COLLATE NOCASE ORDER BY CASE WHEN idx = @id THEN 0 ELSE 1 END, id LIMIT 1;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@field", field);
        command.Parameters.AddWithValue("@id", id);
        var value = command.ExecuteScalar();
        if (value is not null and not DBNull) return Convert.ToString(value)!;
        return SqliteRowService.ResolveColumnName(connection, transaction, table, field) ?? field.ToLowerInvariant();
    }

    private static bool LocalizationExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        uint id,
        string field)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM localized_texts WHERE tbl_name = @table COLLATE NOCASE AND tbl_column_name = @field COLLATE NOCASE AND idx = @id);";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@field", field);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static string RequireTable(SqliteConnection connection, SqliteTransaction transaction, string requestedTable) =>
        SqliteRowService.ResolveTableName(connection, transaction, requestedTable)
        ?? throw new ContentStudioException($"Table '{requestedTable}' does not exist in this compact database.");

    private static string RequireColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string requestedColumn) =>
        SqliteRowService.ResolveColumnName(connection, transaction, table, requestedColumn)
        ?? throw new ContentStudioException($"Column '{requestedColumn}' does not exist in table '{table}'.");

}
