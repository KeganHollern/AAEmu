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
            row[language] = string.Empty;
            row[$"{language}_ver"] = 0;
        }
        var assignedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (requestedLanguage, text) in values)
        {
            var language = CanonicalLanguage(requestedLanguage);
            if (!assignedLanguages.Add(language))
            {
                throw new ContentStudioException($"Localization language '{language}' was provided more than once.");
            }
            row[language] = text;
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
        var assignedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameterIndex = 0;
        foreach (var (requestedLanguage, text) in values)
        {
            var language = CanonicalLanguage(requestedLanguage);
            if (!assignedLanguages.Add(language))
            {
                throw new ContentStudioException($"Localization language '{language}' was provided more than once.");
            }
            var parameter = $"@value{parameterIndex++}";
            assignments.Add($"{BaselineVerifier.QuoteIdentifier(language)} = {parameter}");
            command.Parameters.AddWithValue(parameter, text);
        }

        if (assignments.Count == 0) return;

        command.CommandText = $"UPDATE localized_texts SET {string.Join(", ", assignments)} WHERE tbl_name = @table COLLATE NOCASE AND tbl_column_name = @column COLLATE NOCASE AND idx = @id;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        command.Parameters.AddWithValue("@id", entityId);
        command.ExecuteNonQuery();
    }

    private static string CanonicalLanguage(string requestedLanguage) =>
        Languages.FirstOrDefault(value => value.Equals(requestedLanguage, StringComparison.OrdinalIgnoreCase))
        ?? throw new ContentStudioException($"Unsupported localization language '{requestedLanguage}'.");
}
