using AAEmu.ContentStudio.Core.Models;

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
        foreach (var table in ContentTables)
        {
            var baselineRows = Count(baseline, table);
            var artifactRows = Count(artifact, table);
            result.Tables.Add(new DatabaseTableDiff(table, baselineRows, artifactRows, artifactRows - baselineRows));
        }
        return result;
    }

    private static long Count(Microsoft.Data.Sqlite.SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BaselineVerifier.QuoteIdentifier(table)};";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
