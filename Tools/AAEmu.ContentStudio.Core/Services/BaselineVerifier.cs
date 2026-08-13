using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class BaselineVerifier
{
    public ValidationReport Verify(string compactPath, BaselineDescriptor descriptor, bool verifyHash = true)
    {
        var report = new ValidationReport();
        var fullPath = Path.GetFullPath(compactPath);
        if (!File.Exists(fullPath))
        {
            report.AddError("baseline.missing", $"The compact database does not exist: {fullPath}", fullPath);
            return report;
        }

        var file = new FileInfo(fullPath);
        if (file.Length != descriptor.Length)
        {
            report.AddError("baseline.length", $"Expected {descriptor.Length} bytes but found {file.Length} bytes.", fullPath);
        }

        if (verifyHash)
        {
            var hash = FileHashService.CalculateSha256(fullPath);
            if (!hash.Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                report.AddError("baseline.hash", $"Expected SHA-256 {descriptor.Sha256} but found {hash}.", fullPath);
            }
        }

        try
        {
            using var connection = CompactConnectionFactory.OpenReadOnly(fullPath);
            VerifySchema(connection, descriptor, report);
            var integrity = ExecuteScalarString(connection, "PRAGMA integrity_check;");
            if (!integrity.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                report.AddError("baseline.integrity", $"SQLite integrity check returned: {integrity}", fullPath);
            }
        }
        catch (SqliteException exception)
        {
            report.AddError("baseline.sqlite", exception.Message, fullPath);
        }

        if (report.IsValid)
        {
            report.AddInformation("baseline.valid", $"Baseline {descriptor.Key} is valid.", fullPath);
        }

        return report;
    }

    private static void VerifySchema(SqliteConnection connection, BaselineDescriptor descriptor, ValidationReport report)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
        var tableCount = Convert.ToInt32(countCommand.ExecuteScalar());
        if (tableCount != descriptor.TableCount)
        {
            report.AddError("baseline.tableCount", $"Expected {descriptor.TableCount} tables but found {tableCount}.");
        }

        foreach (var (table, requiredColumns) in descriptor.RequiredTables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var columns = ReadColumns(connection, table);
            if (columns.Count == 0)
            {
                report.AddError("schema.tableMissing", $"Required table '{table}' is missing.", entity: table);
                continue;
            }

            foreach (var requiredColumn in requiredColumns)
            {
                if (!columns.Contains(requiredColumn, StringComparer.OrdinalIgnoreCase))
                {
                    report.AddError("schema.columnMissing", $"Required column '{table}.{requiredColumn}' is missing.", entity: table);
                }
            }
        }
    }

    private static List<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }

    private static string ExecuteScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    internal static string QuoteIdentifier(string identifier)
    {
        if (identifier.Length == 0 || identifier.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new ContentStudioException($"Invalid SQLite identifier: {identifier}");
        }

        return $"\"{identifier}\"";
    }
}
