using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class CompactCatalogService
{
    public IReadOnlyList<TableSchema> ListSchema(string compactPath, string? nameFilter = null)
    {
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND (@filter = '' OR name LIKE @filter) ORDER BY name;";
        command.Parameters.AddWithValue("@filter", string.IsNullOrWhiteSpace(nameFilter) ? string.Empty : $"%{nameFilter}%");
        using var reader = command.ExecuteReader();
        var tableNames = new List<string>();
        while (reader.Read())
        {
            tableNames.Add(reader.GetString(0));
        }

        return tableNames.Select(table => ReadTableSchema(connection, table)).ToList();
    }

    public IReadOnlyList<ItemSearchResult> SearchItems(string compactPath, string query, string language = "en_us", int limit = 50)
    {
        ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.id,
                   COALESCE(NULLIF(lt.{BaselineVerifier.QuoteIdentifier(language)}, ''), i.name, ''),
                   COALESCE(i.category_id, 0),
                   COALESCE(i.price, 0)
              FROM items i
              LEFT JOIN localized_texts lt
                ON lt.tbl_name = 'items'
               AND lt.tbl_column_name = 'name'
               AND lt.idx = i.id
             WHERE CAST(i.id AS TEXT) = @exact
                OR LOWER(COALESCE(lt.{BaselineVerifier.QuoteIdentifier(language)}, i.name, '')) LIKE @query
             ORDER BY CASE
                        WHEN CAST(i.id AS TEXT) = @exact THEN 0
                        WHEN LOWER(COALESCE(NULLIF(lt.{BaselineVerifier.QuoteIdentifier(language)}, ''), i.name, '')) = @normalized THEN 1
                        WHEN LOWER(COALESCE(NULLIF(lt.{BaselineVerifier.QuoteIdentifier(language)}, ''), i.name, '')) LIKE @prefix THEN 2
                        ELSE 3
                      END,
                      LOWER(COALESCE(lt.{BaselineVerifier.QuoteIdentifier(language)}, i.name, '')),
                      i.id
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@exact", query.Trim());
        command.Parameters.AddWithValue("@normalized", query.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("@prefix", $"{query.Trim().ToLowerInvariant()}%");
        command.Parameters.AddWithValue("@query", $"%{query.Trim().ToLowerInvariant()}%");
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var result = new List<ItemSearchResult>();
        while (reader.Read())
        {
            result.Add(new ItemSearchResult(
                Convert.ToUInt32(reader.GetInt64(0)),
                reader.GetString(1),
                Convert.ToUInt32(reader.GetInt64(2)),
                reader.GetInt32(3)));
        }

        return result;
    }

    public IReadOnlyList<RecipeLookupResult> SearchRecipes(string compactPath, string query, string language = "en_us", int limit = 30)
    {
        ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        var languageColumn = BaselineVerifier.QuoteIdentifier(language);
        command.CommandText = $"""
            SELECT c.id,
                   COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, ''),
                   (SELECT COUNT(*) FROM craft_materials cm WHERE cm.craft_id = c.id),
                   (SELECT COUNT(*) FROM craft_products cp WHERE cp.craft_id = c.id),
                   COALESCE(c.req_doodad_id, 0)
              FROM crafts c
              LEFT JOIN localized_texts lt
                ON lt.tbl_name = 'crafts'
               AND lt.tbl_column_name = 'title'
               AND lt.idx = c.id
             WHERE CAST(c.id AS TEXT) = @exact
                OR LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, '')) LIKE @query
             ORDER BY CASE
                        WHEN CAST(c.id AS TEXT) = @exact THEN 0
                        WHEN LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, '')) = @normalized THEN 1
                        WHEN LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, '')) LIKE @prefix THEN 2
                        ELSE 3
                      END,
                      LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, '')),
                      c.id
             LIMIT @limit;
            """;
        AddLookupParameters(command, query, limit);
        using var reader = command.ExecuteReader();
        var result = new List<RecipeLookupResult>();
        while (reader.Read())
        {
            result.Add(new RecipeLookupResult(
                Convert.ToUInt32(reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                Convert.ToUInt32(reader.GetInt64(4))));
        }

        return result;
    }

    public IReadOnlyList<RecipeLookupResult> GetRecipeLookups(string compactPath, IEnumerable<uint> recipeIds, string language = "en_us")
    {
        ValidateLanguageColumn(language);
        var ids = recipeIds.Distinct().Take(900).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        var parameters = new List<string>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            var name = $"@id{index}";
            parameters.Add(name);
            command.Parameters.AddWithValue(name, ids[index]);
        }
        var languageColumn = BaselineVerifier.QuoteIdentifier(language);
        command.CommandText = $"""
            SELECT c.id,
                   COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, ''),
                   (SELECT COUNT(*) FROM craft_materials cm WHERE cm.craft_id = c.id),
                   (SELECT COUNT(*) FROM craft_products cp WHERE cp.craft_id = c.id),
                   COALESCE(c.req_doodad_id, 0)
              FROM crafts c
              LEFT JOIN localized_texts lt
                ON lt.tbl_name = 'crafts'
               AND lt.tbl_column_name = 'title'
               AND lt.idx = c.id
             WHERE c.id IN ({string.Join(", ", parameters)})
             ORDER BY LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, '')), c.id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<RecipeLookupResult>();
        while (reader.Read())
        {
            result.Add(new RecipeLookupResult(
                Convert.ToUInt32(reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                Convert.ToUInt32(reader.GetInt64(4))));
        }
        return result;
    }

    public IReadOnlyList<WorkbenchLookupResult> SearchWorkbenches(string compactPath, string query, string language = "en_us", int limit = 30)
    {
        ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        var languageColumn = BaselineVerifier.QuoteIdentifier(language);
        command.CommandText = $"""
            WITH candidates AS MATERIALIZED (
                SELECT d.id,
                       COALESCE(NULLIF(lt.{languageColumn}, ''), d.name, '') AS resolved_name,
                       COALESCE(d.model, '') AS model,
                       CASE
                         WHEN CAST(d.id AS TEXT) = @exact THEN 0
                         WHEN LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), d.name, '')) = @normalized THEN 1
                         WHEN LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), d.name, '')) LIKE @prefix THEN 2
                         ELSE 3
                       END AS match_order
                  FROM doodad_almighties d
                  LEFT JOIN localized_texts lt
                    ON lt.tbl_name = 'doodad_almighties'
                   AND lt.tbl_column_name = 'name'
                   AND lt.idx = d.id
                 WHERE CAST(d.id AS TEXT) = @exact
                    OR LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), d.name, '')) LIKE @query
                 ORDER BY match_order, LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), d.name, '')), d.id
                 LIMIT 200
            )
            SELECT d.id,
                   d.resolved_name,
                   d.model,
                   (SELECT COUNT(*) FROM doodad_func_groups groups WHERE groups.doodad_almighty_id = d.id),
                   (SELECT COUNT(*)
                      FROM doodad_funcs funcs
                      JOIN doodad_func_groups groups ON groups.id = funcs.doodad_func_group_id
                     WHERE groups.doodad_almighty_id = d.id),
                   (SELECT COUNT(DISTINCT links.craft_id)
                      FROM doodad_funcs funcs
                      JOIN doodad_func_groups groups ON groups.id = funcs.doodad_func_group_id
                      JOIN doodad_func_craft_packs payload
                        ON funcs.actual_func_type = 'DoodadFuncCraftPack'
                       AND payload.id = funcs.actual_func_id
                      JOIN craft_pack_crafts links ON links.craft_pack_id = payload.craft_pack_id
                     WHERE groups.doodad_almighty_id = d.id)
              FROM candidates d
             WHERE EXISTS (
                   SELECT 1
                     FROM doodad_funcs funcs
                     JOIN doodad_func_groups groups ON groups.id = funcs.doodad_func_group_id
                    WHERE groups.doodad_almighty_id = d.id
                      AND funcs.actual_func_type = 'DoodadFuncCraftPack')
             ORDER BY d.match_order, LOWER(d.resolved_name), d.id
             LIMIT @limit;
            """;
        AddLookupParameters(command, query, limit);
        using var reader = command.ExecuteReader();
        var result = new List<WorkbenchLookupResult>();
        while (reader.Read())
        {
            result.Add(new WorkbenchLookupResult(
                Convert.ToUInt32(reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5)));
        }

        return result;
    }

    public IReadOnlyList<ItemRecipeRelationship> GetRecipesConsumingItem(string compactPath, uint itemId, string language = "en_us")
    {
        ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        var languageColumn = BaselineVerifier.QuoteIdentifier(language);
        command.CommandText = $"""
            SELECT c.id,
                   COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, ''),
                   SUM(cm.amount),
                   COALESCE(c.req_doodad_id, 0)
              FROM craft_materials cm
              JOIN crafts c ON c.id = cm.craft_id
              LEFT JOIN localized_texts lt
                ON lt.tbl_name = 'crafts'
               AND lt.tbl_column_name = 'title'
               AND lt.idx = c.id
             WHERE cm.item_id = @itemId
             GROUP BY c.id, COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, ''), c.req_doodad_id
             ORDER BY LOWER(COALESCE(NULLIF(lt.{languageColumn}, ''), c.title, '')), c.id;
            """;
        command.Parameters.AddWithValue("@itemId", itemId);
        using var reader = command.ExecuteReader();
        var result = new List<ItemRecipeRelationship>();
        while (reader.Read())
        {
            result.Add(new ItemRecipeRelationship(
                Convert.ToUInt32(reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                Convert.ToUInt32(reader.GetInt64(3))));
        }

        return result;
    }

    public RecipeGraph? GetRecipe(string compactPath, uint recipeId, string language = "en_us")
    {
        ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.id,
                   COALESCE(NULLIF(title_text.{BaselineVerifier.QuoteIdentifier(language)}, ''), c.title, ''),
                   COALESCE(NULLIF(description_text.{BaselineVerifier.QuoteIdentifier(language)}, ''), c.desc, ''),
                   COALESCE(c.skill_id, 0),
                   COALESCE(s.consume_lp, 0),
                   COALESCE(s.casting_time, 0),
                   COALESCE(c.req_doodad_id, 0)
              FROM crafts c
              LEFT JOIN skills s ON s.id = c.skill_id
              LEFT JOIN localized_texts title_text
                ON title_text.tbl_name = 'crafts'
               AND title_text.tbl_column_name = 'title'
               AND title_text.idx = c.id
              LEFT JOIN localized_texts description_text
                ON description_text.tbl_name = 'crafts'
               AND description_text.tbl_column_name = 'desc'
               AND description_text.idx = c.id
             WHERE c.id = @id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", recipeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var graph = new RecipeGraph
        {
            Id = Convert.ToUInt32(reader.GetInt64(0)),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            SkillId = Convert.ToUInt32(reader.GetInt64(3)),
            LaborCost = reader.GetInt32(4),
            CastingTime = reader.GetInt32(5),
            RequiredDoodadId = Convert.ToUInt32(reader.GetInt64(6))
        };
        reader.Close();
        graph.CraftPackIds = ReadUIntList(connection, "SELECT craft_pack_id FROM craft_pack_crafts WHERE craft_id = @id ORDER BY craft_pack_id;", recipeId);
        graph.Materials = ReadRecipeMaterials(connection, recipeId, language);
        graph.Products = ReadRecipeProducts(connection, recipeId, language);
        return graph;
    }

    public WorkbenchGraph? GetWorkbench(string compactPath, uint doodadId, string language = "en_us")
    {
        ValidateLanguageColumn(language);
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.id,
                   COALESCE(NULLIF(lt.{BaselineVerifier.QuoteIdentifier(language)}, ''), d.name, ''),
                   COALESCE(d.model, '')
              FROM doodad_almighties d
              LEFT JOIN localized_texts lt
                ON lt.tbl_name = 'doodad_almighties'
               AND lt.tbl_column_name = 'name'
               AND lt.idx = d.id
             WHERE d.id = @id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", doodadId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var graph = new WorkbenchGraph
        {
            Id = Convert.ToUInt32(reader.GetInt64(0)),
            Name = reader.GetString(1),
            Model = reader.GetString(2)
        };
        reader.Close();

        var groups = ReadWorkbenchGroups(connection, doodadId);
        graph.Groups = groups;
        graph.CraftPackIds = groups
            .SelectMany(group => group.Functions)
            .Where(function => function.CraftPackId.HasValue)
            .Select(function => function.CraftPackId!.Value)
            .Distinct()
            .Order()
            .ToList();
        graph.RecipeIds = graph.CraftPackIds
            .SelectMany(packId => ReadUIntList(connection, "SELECT craft_id FROM craft_pack_crafts WHERE craft_pack_id = @id ORDER BY craft_id;", packId))
            .Distinct()
            .Order()
            .ToList();
        return graph;
    }

    private static TableSchema ReadTableSchema(SqliteConnection connection, string table)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {BaselineVerifier.QuoteIdentifier(table)};";
        var count = Convert.ToInt64(countCommand.ExecuteScalar());

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({BaselineVerifier.QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<TableColumn>();
        while (reader.Read())
        {
            columns.Add(new TableColumn(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)));
        }

        return new TableSchema(table, count, columns);
    }

    private static List<RecipeMaterial> ReadRecipeMaterials(SqliteConnection connection, uint recipeId, string language)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT cm.id,
                   cm.item_id,
                   COALESCE(NULLIF(lt.{BaselineVerifier.QuoteIdentifier(language)}, ''), i.name, ''),
                   cm.amount,
                   COALESCE(cm.main_grade, 0),
                   COALESCE(cm.require_grade, 0)
              FROM craft_materials cm
              LEFT JOIN items i ON i.id = cm.item_id
              LEFT JOIN localized_texts lt
                ON lt.tbl_name = 'items'
               AND lt.tbl_column_name = 'name'
               AND lt.idx = cm.item_id
             WHERE cm.craft_id = @id
             ORDER BY cm.id;
            """;
        command.Parameters.AddWithValue("@id", recipeId);
        using var reader = command.ExecuteReader();
        var result = new List<RecipeMaterial>();
        while (reader.Read())
        {
            result.Add(new RecipeMaterial(
                Convert.ToUInt32(reader.GetInt64(0)),
                Convert.ToUInt32(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                ReadBoolean(reader, 4),
                reader.GetInt32(5)));
        }

        return result;
    }

    private static List<RecipeProduct> ReadRecipeProducts(SqliteConnection connection, uint recipeId, string language)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT cp.id,
                   cp.item_id,
                   COALESCE(NULLIF(lt.{BaselineVerifier.QuoteIdentifier(language)}, ''), i.name, ''),
                   cp.amount,
                   COALESCE(cp.rate, 0),
                   COALESCE(cp.use_grade, 0),
                   COALESCE(cp.item_grade_id, 0),
                   COALESCE(cp.show_lower_crafts, 0)
              FROM craft_products cp
              LEFT JOIN items i ON i.id = cp.item_id
              LEFT JOIN localized_texts lt
                ON lt.tbl_name = 'items'
               AND lt.tbl_column_name = 'name'
               AND lt.idx = cp.item_id
             WHERE cp.craft_id = @id
             ORDER BY cp.id;
            """;
        command.Parameters.AddWithValue("@id", recipeId);
        using var reader = command.ExecuteReader();
        var result = new List<RecipeProduct>();
        while (reader.Read())
        {
            result.Add(new RecipeProduct(
                Convert.ToUInt32(reader.GetInt64(0)),
                Convert.ToUInt32(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                ReadBoolean(reader, 7),
                ReadBoolean(reader, 5),
                Convert.ToUInt32(reader.GetInt64(6))));
        }

        return result;
    }

    private static List<WorkbenchFunctionGroup> ReadWorkbenchGroups(SqliteConnection connection, uint doodadId)
    {
        using var groupsCommand = connection.CreateCommand();
        groupsCommand.CommandText = "SELECT id, doodad_func_group_kind_id, COALESCE(model, '') FROM doodad_func_groups WHERE doodad_almighty_id = @id ORDER BY id;";
        groupsCommand.Parameters.AddWithValue("@id", doodadId);
        using var groupReader = groupsCommand.ExecuteReader();
        var rawGroups = new List<(uint Id, uint KindId, string Model)>();
        while (groupReader.Read())
        {
            rawGroups.Add((Convert.ToUInt32(groupReader.GetInt64(0)), Convert.ToUInt32(groupReader.GetInt64(1)), groupReader.GetString(2)));
        }
        groupReader.Close();

        var result = new List<WorkbenchFunctionGroup>();
        foreach (var group in rawGroups)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT df.id,
                       df.doodad_func_group_id,
                       df.actual_func_type,
                       df.actual_func_id,
                       COALESCE(df.next_phase, -1),
                       COALESCE(df.func_skill_id, 0),
                       CASE WHEN df.actual_func_type = 'DoodadFuncCraftPack' THEN cp.craft_pack_id ELSE NULL END
                  FROM doodad_funcs df
                  LEFT JOIN doodad_func_craft_packs cp
                    ON df.actual_func_type = 'DoodadFuncCraftPack'
                   AND cp.id = df.actual_func_id
                 WHERE df.doodad_func_group_id = @id
                 ORDER BY df.id;
                """;
            command.Parameters.AddWithValue("@id", group.Id);
            using var reader = command.ExecuteReader();
            var functions = new List<WorkbenchFunction>();
            while (reader.Read())
            {
                functions.Add(new WorkbenchFunction(
                    Convert.ToUInt32(reader.GetInt64(0)),
                    Convert.ToUInt32(reader.GetInt64(1)),
                    reader.GetString(2),
                    Convert.ToUInt32(reader.GetInt64(3)),
                    reader.GetInt32(4),
                    Convert.ToUInt32(reader.GetInt64(5)),
                    reader.IsDBNull(6) ? null : Convert.ToUInt32(reader.GetInt64(6))));
            }

            result.Add(new WorkbenchFunctionGroup(group.Id, group.KindId, group.Model, functions));
        }

        return result;
    }

    private static List<uint> ReadUIntList(SqliteConnection connection, string sql, uint id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        var result = new List<uint>();
        while (reader.Read())
        {
            result.Add(Convert.ToUInt32(reader.GetInt64(0)));
        }

        return result;
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool boolean => boolean,
            long number => number != 0,
            string text => text.Equals("t", StringComparison.OrdinalIgnoreCase) || text == "1",
            _ => Convert.ToBoolean(value)
        };
    }

    private static void AddLookupParameters(SqliteCommand command, string query, int limit)
    {
        var normalized = query.Trim().ToLowerInvariant();
        command.Parameters.AddWithValue("@exact", query.Trim());
        command.Parameters.AddWithValue("@normalized", normalized);
        command.Parameters.AddWithValue("@prefix", $"{normalized}%");
        command.Parameters.AddWithValue("@query", $"%{normalized}%");
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
    }

    internal static void ValidateLanguageColumn(string language)
    {
        BaselineVerifier.QuoteIdentifier(language);
        if (language.EndsWith("_ver", StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStudioException($"'{language}' is not a localization value column.");
        }
    }
}
