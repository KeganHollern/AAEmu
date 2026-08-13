using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

internal static class LocalizationCompiler
{
    internal static readonly string[] Languages = ["ko", "en_us", "zh_cn", "ja", "ru", "zh_tw", "de", "fr"];

    public static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        uint rowId,
        string table,
        string column,
        uint entityId,
        IReadOnlyDictionary<string, string> values)
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = rowId,
            ["tbl_name"] = table,
            ["tbl_column_name"] = column,
            ["idx"] = entityId
        };

        foreach (var language in Languages)
        {
            row[language] = values.TryGetValue(language, out var text) ? text : string.Empty;
            row[$"{language}_ver"] = 0;
        }

        SqliteRowService.Insert(connection, transaction, "localized_texts", row);
    }

    public static void Update(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        uint entityId,
        IReadOnlyDictionary<string, string> values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var assignments = new List<string>();
        foreach (var language in Languages)
        {
            var parameter = $"@{language}";
            assignments.Add($"{BaselineVerifier.QuoteIdentifier(language)} = {parameter}");
            command.Parameters.AddWithValue(parameter, values.GetValueOrDefault(language, string.Empty));
        }
        command.CommandText = $"UPDATE localized_texts SET {string.Join(", ", assignments)} WHERE tbl_name = @table AND tbl_column_name = @column AND idx = @id;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        command.Parameters.AddWithValue("@id", entityId);
        command.ExecuteNonQuery();
    }
}
