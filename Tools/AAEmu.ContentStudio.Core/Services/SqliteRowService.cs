using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

internal static class SqliteRowService
{
    public static string? ResolveTableName(SqliteConnection connection, SqliteTransaction? transaction, string requestedTable)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = @table COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("@table", requestedTable);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public static string? ResolveColumnName(SqliteConnection connection, SqliteTransaction? transaction, string table, string requestedColumn)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM pragma_table_info(@table) WHERE name = @column COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", requestedColumn);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, string table, uint id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {BaselineVerifier.QuoteIdentifier(table)} WHERE id = @id);";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public static void CloneById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        uint sourceId,
        IReadOnlyDictionary<string, object?> overrides)
    {
        var columns = ReadColumns(connection, transaction, table);
        if (columns.Count == 0)
        {
            throw new ContentStudioException($"Table '{table}' does not exist or has no columns.");
        }

        var expressions = new List<string>(columns.Count);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@sourceId", sourceId);
        foreach (var column in columns)
        {
            if (overrides.TryGetValue(column, out var value))
            {
                var parameterName = $"@value{expressions.Count}";
                expressions.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
            }
            else
            {
                expressions.Add(BaselineVerifier.QuoteIdentifier(column));
            }
        }

        var identifiers = string.Join(", ", columns.Select(BaselineVerifier.QuoteIdentifier));
        command.CommandText = $"INSERT INTO {BaselineVerifier.QuoteIdentifier(table)} ({identifiers}) SELECT {string.Join(", ", expressions)} FROM {BaselineVerifier.QuoteIdentifier(table)} WHERE id = @sourceId;";
        if (command.ExecuteNonQuery() != 1)
        {
            throw new ContentStudioException($"Could not clone {table} row {sourceId}; the source row does not exist.");
        }
    }

    public static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var columns = values.Keys.ToList();
        var parameters = new List<string>(columns.Count);
        for (var index = 0; index < columns.Count; index++)
        {
            var parameterName = $"@value{index}";
            parameters.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, values[columns[index]] ?? DBNull.Value);
        }

        command.CommandText = $"INSERT INTO {BaselineVerifier.QuoteIdentifier(table)} ({string.Join(", ", columns.Select(BaselineVerifier.QuoteIdentifier))}) VALUES ({string.Join(", ", parameters)});";
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<uint> ReadIds(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string parameterName,
        uint value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameterName, value);
        using var reader = command.ExecuteReader();
        var result = new List<uint>();
        while (reader.Read())
        {
            result.Add(Convert.ToUInt32(reader.GetInt64(0)));
        }

        return result;
    }

    private static List<string> ReadColumns(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({BaselineVerifier.QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
