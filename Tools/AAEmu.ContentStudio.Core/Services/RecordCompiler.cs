using System.Globalization;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

internal sealed class RecordCompiler
{
    public IReadOnlyList<ContentChange> Compile(SqliteConnection connection, SqliteTransaction transaction, RecordDefinition definition)
    {
        var values = ConvertValues(connection, transaction, definition.Table, definition.Values);
        if (definition.Mode == RecordChangeMode.Duplicate)
        {
            values["id"] = definition.Id;
            SqliteRowService.CloneById(connection, transaction, definition.Table, definition.SourceId, values);
            foreach (var child in definition.Children)
            {
                var childValues = ConvertValues(connection, transaction, child.Table, child.Values);
                childValues["id"] = child.Id;
                childValues[child.OwnerColumn] = definition.Id;
                SqliteRowService.CloneById(connection, transaction, child.Table, child.SourceId, childValues);
            }
        }
        else
        {
            UpdateById(connection, transaction, definition.Table, definition.Id, values);
            foreach (var child in definition.Children)
            {
                UpdateById(connection, transaction, child.Table, child.Id, ConvertValues(connection, transaction, child.Table, child.Values));
            }
        }

        foreach (var (field, localizedValues) in definition.Localizations)
        {
            if (definition.Mode == RecordChangeMode.Modify && LocalizationExists(connection, transaction, definition.Table, definition.Id, field))
            {
                LocalizationCompiler.Update(connection, transaction, definition.Table, field, definition.Id, localizedValues);
            }
            else
            {
                if (!definition.LocalizationRowIds.TryGetValue(field, out var rowId))
                {
                    throw new ContentStudioException($"The translated field '{field}' is missing its reserved row ID.");
                }
                LocalizationCompiler.Insert(connection, transaction, rowId, definition.Table, field, definition.Id, localizedValues);
            }
        }

        var action = definition.Mode == RecordChangeMode.Duplicate ? "duplicate" : "modify";
        var summary = definition.Mode == RecordChangeMode.Duplicate
            ? $"Copied {definition.Table} {definition.SourceId} to {definition.Id}, including {definition.Children.Count} directly owned rows."
            : $"Changed {definition.Table} {definition.Id} while preserving the pristine baseline.";
        return [new ContentChange("record", definition.Key, definition.Id, action, summary)];
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
            if (!types.TryGetValue(name, out var type) || name.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            if (CatalogRecordService.IsCompactNull(value))
            {
                result[name] = null;
            }
            else if (type.Contains("INT", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw new ContentStudioException($"'{CatalogRecordService.FriendlyName(name)}' must be a whole number.");
                result[name] = integer;
            }
            else if (type.Equals("NUM", StringComparison.OrdinalIgnoreCase))
            {
                if (value is "t" or "f" or "true" or "false" or "T" or "F" or "True" or "False")
                    result[name] = value;
                else if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    result[name] = integer;
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    result[name] = number;
                else
                    throw new ContentStudioException($"'{CatalogRecordService.FriendlyName(name)}' must be a number or a Yes/No value.");
            }
            else if (type.Contains("REAL", StringComparison.OrdinalIgnoreCase) || type.Contains("FLOA", StringComparison.OrdinalIgnoreCase) || type.Contains("DOUB", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new ContentStudioException($"'{CatalogRecordService.FriendlyName(name)}' must be a number.");
                result[name] = number;
            }
            else
            {
                result[name] = value;
            }
        }
        return result;
    }

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

    private static bool LocalizationExists(SqliteConnection connection, SqliteTransaction transaction, string table, uint id, string field)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM localized_texts WHERE tbl_name = @table AND tbl_column_name = @field AND idx = @id);";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@field", field);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }
}
