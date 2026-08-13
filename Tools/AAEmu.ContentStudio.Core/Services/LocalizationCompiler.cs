using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

internal static class LocalizationCompiler
{
    private static readonly string[] Languages = ["ko", "en_us", "zh_cn", "ja", "ru", "zh_tw", "de", "fr"];

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
}
