using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public static class CompactConnectionFactory
{
    public static SqliteConnection OpenReadOnly(string path)
    {
        EnsureDatabaseExists(path);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    public static SqliteConnection OpenReadWrite(string path)
    {
        EnsureDatabaseExists(path);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void EnsureDatabaseExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new ContentStudioException($"Compact database does not exist: {Path.GetFullPath(path)}");
        }
    }
}
