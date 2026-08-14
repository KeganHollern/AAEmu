using AAEmu.ContentStudio.Core.Models;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class DatabaseDiffService
{
    private static readonly string[] ContentTables =
    [
        "crafts", "skills", "skill_effects", "craft_materials", "craft_products", "craft_packs", "craft_pack_crafts",
        "doodad_almighties", "doodad_func_groups", "doodad_funcs", "doodad_phase_funcs", "doodad_func_craft_packs", "localized_texts"
    ];

    public DatabaseDiffReport Compare(string baselinePath, string artifactPath)
    {
        var result = new DatabaseDiffReport
        {
            BaselinePath = Path.GetFullPath(baselinePath),
            ArtifactPath = Path.GetFullPath(artifactPath)
        };
        using var baseline = CompactConnectionFactory.OpenReadOnly(baselinePath);
        using var artifact = CompactConnectionFactory.OpenReadOnly(artifactPath);
        using (var attach = artifact.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE @path AS baseline;";
            attach.Parameters.AddWithValue("@path", Path.GetFullPath(baselinePath));
            attach.ExecuteNonQuery();
        }
        foreach (var table in ContentTables)
        {
            var baselineRows = Count(baseline, table);
            var artifactRows = Count(artifact, table);
            var changedCells = ReadChangedCells(artifact, table, out var modifiedRows);
            result.Tables.Add(new DatabaseTableDiff(table, baselineRows, artifactRows, artifactRows - baselineRows)
            {
                ModifiedRows = modifiedRows,
                ChangedCells = changedCells
            });
        }
        return result;
    }

    private static long Count(Microsoft.Data.Sqlite.SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BaselineVerifier.QuoteIdentifier(table)};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static List<DatabaseCellDiff> ReadChangedCells(SqliteConnection connection, string table, out long modifiedRows)
    {
        var columns = ReadColumns(connection, "main", table)
            .Intersect(ReadColumns(connection, "baseline", table), StringComparer.OrdinalIgnoreCase)
            .Where(column => !column.Equals("id", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (columns.Count == 0)
        {
            modifiedRows = 0;
            return [];
        }

        var changedPredicate = string.Join(" OR ", columns.Select(column =>
            $"NOT (artifact.{BaselineVerifier.QuoteIdentifier(column)} IS pristine.{BaselineVerifier.QuoteIdentifier(column)})"));
        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM main.{BaselineVerifier.QuoteIdentifier(table)} artifact JOIN baseline.{BaselineVerifier.QuoteIdentifier(table)} pristine ON pristine.id = artifact.id WHERE {changedPredicate};";
        modifiedRows = Convert.ToInt64(count.ExecuteScalar());

        using var idsCommand = connection.CreateCommand();
        idsCommand.CommandText = $"SELECT artifact.id FROM main.{BaselineVerifier.QuoteIdentifier(table)} artifact JOIN baseline.{BaselineVerifier.QuoteIdentifier(table)} pristine ON pristine.id = artifact.id WHERE {changedPredicate} ORDER BY artifact.id LIMIT 1000;";
        var ids = new List<uint>();
        using (var reader = idsCommand.ExecuteReader())
        {
            while (reader.Read()) ids.Add(Convert.ToUInt32(reader.GetInt64(0)));
        }

        var result = new List<DatabaseCellDiff>();
        foreach (var id in ids)
        {
            foreach (var column in columns)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT pristine.{BaselineVerifier.QuoteIdentifier(column)}, artifact.{BaselineVerifier.QuoteIdentifier(column)} FROM main.{BaselineVerifier.QuoteIdentifier(table)} artifact JOIN baseline.{BaselineVerifier.QuoteIdentifier(table)} pristine ON pristine.id = artifact.id WHERE artifact.id = @id AND NOT (artifact.{BaselineVerifier.QuoteIdentifier(column)} IS pristine.{BaselineVerifier.QuoteIdentifier(column)});";
                command.Parameters.AddWithValue("@id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) continue;
                result.Add(new DatabaseCellDiff(id, column, Format(reader.GetValue(0)), Format(reader.GetValue(1))));
            }
        }
        return result;
    }

    private static List<string> ReadColumns(SqliteConnection connection, string schema, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {schema}.table_info({BaselineVerifier.QuoteIdentifier(table)});";
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static string? Format(object value) => value is DBNull
        ? null
        : Convert.ToString(value, CultureInfo.InvariantCulture);
}
