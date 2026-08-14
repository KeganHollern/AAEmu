using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

internal sealed class WorkbenchCompiler
{
    public IReadOnlyList<ContentChange> Compile(SqliteConnection connection, SqliteTransaction transaction, WorkbenchDefinition workbench)
    {
        var doodadOverrides = new Dictionary<string, object?>
        {
            ["id"] = workbench.Id,
            ["name"] = workbench.Names.GetValueOrDefault("en_us", workbench.Key)
        };
        if (!string.IsNullOrWhiteSpace(workbench.ModelOverride))
        {
            doodadOverrides["model"] = workbench.ModelOverride;
        }
        SqliteRowService.CloneById(connection, transaction, "doodad_almighties", workbench.SourceDoodadId, doodadOverrides);
        SqliteRowService.Insert(connection, transaction, "craft_packs", new Dictionary<string, object?>
        {
            ["id"] = workbench.CraftPack.Id,
            ["name"] = workbench.CraftPack.Name
        });

        var sourceGroups = SqliteRowService.ReadIds(connection, transaction, "SELECT id FROM doodad_func_groups WHERE doodad_almighty_id = @id ORDER BY id;", "@id", workbench.SourceDoodadId);
        foreach (var sourceGroupId in sourceGroups)
        {
            SqliteRowService.CloneById(connection, transaction, "doodad_func_groups", sourceGroupId, new Dictionary<string, object?>
            {
                ["id"] = workbench.RowIds.FunctionGroups[sourceGroupId],
                ["doodad_almighty_id"] = workbench.Id
            });
        }

        foreach (var sourceGroupId in sourceGroups)
        {
            CloneFunctions(connection, transaction, workbench, sourceGroupId);
            ClonePhaseFunctions(connection, transaction, workbench, sourceGroupId);
        }

        if (workbench.Names.Count > 0)
        {
            if (!workbench.RowIds.Localization.TryGetValue("name", out var rowId))
            {
                rowId = workbench.RowIds.Localization.FirstOrDefault(pair => pair.Key.StartsWith("name:", StringComparison.OrdinalIgnoreCase)).Value;
            }
            if (rowId == 0)
            {
                throw new ContentStudioException("Missing localized_texts ID for workbench name.");
            }
            LocalizationCompiler.Insert(connection, transaction, rowId, "doodad_almighties", "name", workbench.Id, workbench.Names);
        }

        for (var index = 0; index < workbench.RecipeIds.Length; index++)
        {
            SqliteRowService.Insert(connection, transaction, "craft_pack_crafts", new Dictionary<string, object?>
            {
                ["id"] = workbench.RowIds.CraftPackLinks[index],
                ["craft_pack_id"] = workbench.CraftPack.Id,
                ["craft_id"] = workbench.RecipeIds[index]
            });
        }

        return [new ContentChange("workbench", workbench.Key, workbench.Id, "clone", "Created a custom workbench from a proven crafting object and preserved its complete behavior graph.")];
    }

    private static void CloneFunctions(SqliteConnection connection, SqliteTransaction transaction, WorkbenchDefinition workbench, uint sourceGroupId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, actual_func_id, actual_func_type FROM doodad_funcs WHERE doodad_func_group_id = @id ORDER BY id;";
        command.Parameters.AddWithValue("@id", sourceGroupId);
        using var reader = command.ExecuteReader();
        var rows = new List<(uint Id, uint ActualId, string Type)>();
        while (reader.Read())
        {
            rows.Add((Convert.ToUInt32(reader.GetInt64(0)), Convert.ToUInt32(reader.GetInt64(1)), reader.GetString(2)));
        }
        reader.Close();

        foreach (var row in rows)
        {
            var actualId = row.ActualId;
            if (row.Type.Equals("DoodadFuncCraftPack", StringComparison.Ordinal))
            {
                actualId = workbench.RowIds.CraftPackPayloads[row.ActualId];
                SqliteRowService.CloneById(connection, transaction, "doodad_func_craft_packs", row.ActualId, new Dictionary<string, object?>
                {
                    ["id"] = actualId,
                    ["craft_pack_id"] = workbench.CraftPack.Id
                });
            }

            SqliteRowService.CloneById(connection, transaction, "doodad_funcs", row.Id, new Dictionary<string, object?>
            {
                ["id"] = workbench.RowIds.Functions[row.Id],
                ["doodad_func_group_id"] = workbench.RowIds.FunctionGroups[sourceGroupId],
                ["actual_func_id"] = actualId
            });
        }
    }

    private static void ClonePhaseFunctions(SqliteConnection connection, SqliteTransaction transaction, WorkbenchDefinition workbench, uint sourceGroupId)
    {
        var ids = SqliteRowService.ReadIds(connection, transaction, "SELECT id FROM doodad_phase_funcs WHERE doodad_func_group_id = @id ORDER BY id;", "@id", sourceGroupId);
        foreach (var id in ids)
        {
            SqliteRowService.CloneById(connection, transaction, "doodad_phase_funcs", id, new Dictionary<string, object?>
            {
                ["id"] = workbench.RowIds.PhaseFunctions[id],
                ["doodad_func_group_id"] = workbench.RowIds.FunctionGroups[sourceGroupId]
            });
        }
    }
}
