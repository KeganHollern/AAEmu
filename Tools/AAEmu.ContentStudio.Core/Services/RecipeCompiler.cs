using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

internal sealed class RecipeCompiler
{
    public IReadOnlyList<ContentChange> Compile(SqliteConnection connection, SqliteTransaction transaction, RecipeDefinition recipe)
    {
        var skillId = recipe.SkillClone?.Id ?? recipe.SkillId;
        if (recipe.SkillClone is not null)
        {
            CloneSkill(connection, transaction, recipe.SkillClone);
        }

        SqliteRowService.Insert(connection, transaction, "crafts", new Dictionary<string, object?>
        {
            ["id"] = recipe.Id,
            ["title"] = recipe.Names.GetValueOrDefault("en_us", recipe.Key),
            ["cast_delay"] = recipe.CastDelay,
            ["tool_id"] = 0,
            ["skill_id"] = skillId,
            ["wi_id"] = recipe.WorldInteractionId,
            ["desc"] = recipe.Descriptions.GetValueOrDefault("en_us", string.Empty),
            ["milestone_id"] = 0,
            ["req_doodad_id"] = recipe.RequiredDoodadId,
            ["need_bind"] = ToDatabaseBoolean(recipe.NeedBind),
            ["ac_id"] = recipe.ActabilityCategoryId,
            ["actability_limit"] = recipe.ActabilityLimit,
            ["show_upper_crafts"] = ToDatabaseBoolean(recipe.ShowUpperCrafts),
            ["recommend_level"] = recipe.RecommendLevel,
            ["visible_order"] = recipe.VisibleOrder,
            ["translate"] = ToDatabaseBoolean(true)
        });

        foreach (var material in recipe.Materials)
        {
            SqliteRowService.Insert(connection, transaction, "craft_materials", new Dictionary<string, object?>
            {
                ["id"] = material.Id,
                ["craft_id"] = recipe.Id,
                ["item_id"] = material.ItemId,
                ["amount"] = material.Amount,
                ["main_grade"] = ToDatabaseBoolean(material.MainGrade),
                ["require_grade"] = material.RequiredGrade
            });
        }

        foreach (var product in recipe.Products)
        {
            SqliteRowService.Insert(connection, transaction, "craft_products", new Dictionary<string, object?>
            {
                ["id"] = product.Id,
                ["craft_id"] = recipe.Id,
                ["item_id"] = product.ItemId,
                ["amount"] = product.Amount,
                ["rate"] = product.Rate,
                ["show_lower_crafts"] = ToDatabaseBoolean(product.ShowLowerCrafts),
                ["use_grade"] = ToDatabaseBoolean(product.UseGrade),
                ["item_grade_id"] = product.ItemGradeId
            });
        }

        InsertLocalization(connection, transaction, recipe);
        for (var index = 0; index < recipe.CraftPackIds.Length; index++)
        {
            SqliteRowService.Insert(connection, transaction, "craft_pack_crafts", new Dictionary<string, object?>
            {
                ["id"] = recipe.RowIds.CraftPackLinks[index],
                ["craft_pack_id"] = recipe.CraftPackIds[index],
                ["craft_id"] = recipe.Id
            });
        }

        return [new ContentChange("recipe", recipe.Key, recipe.Id, "insert", $"Added {recipe.Materials.Count} materials, {recipe.Products.Count} products, and {recipe.CraftPackIds.Length} pack links.")];
    }

    private static void CloneSkill(SqliteConnection connection, SqliteTransaction transaction, SkillCloneDefinition definition)
    {
        var overrides = new Dictionary<string, object?> { ["id"] = definition.Id };
        if (definition.LaborCost.HasValue)
        {
            overrides["consume_lp"] = definition.LaborCost.Value;
        }
        if (definition.CastingTime.HasValue)
        {
            overrides["casting_time"] = definition.CastingTime.Value;
        }
        SqliteRowService.CloneById(connection, transaction, "skills", definition.SourceId, overrides);

        var sourceEffects = SqliteRowService.ReadIds(connection, transaction, "SELECT id FROM skill_effects WHERE skill_id = @id ORDER BY id;", "@id", definition.SourceId);
        for (var index = 0; index < sourceEffects.Count; index++)
        {
            SqliteRowService.CloneById(connection, transaction, "skill_effects", sourceEffects[index], new Dictionary<string, object?>
            {
                ["id"] = definition.SkillEffectRowIds[index],
                ["skill_id"] = definition.Id
            });
        }
    }

    private static void InsertLocalization(SqliteConnection connection, SqliteTransaction transaction, RecipeDefinition recipe)
    {
        if (recipe.Names.Count > 0)
        {
            LocalizationCompiler.Insert(connection, transaction, RequireLocalizationId(recipe.RowIds.Localization, "title"), "crafts", "title", recipe.Id, recipe.Names);
        }
        if (recipe.Descriptions.Count > 0)
        {
            LocalizationCompiler.Insert(connection, transaction, RequireLocalizationId(recipe.RowIds.Localization, "desc"), "crafts", "desc", recipe.Id, recipe.Descriptions);
        }
    }

    private static uint RequireLocalizationId(Dictionary<string, uint> ids, string key)
    {
        if (!ids.TryGetValue(key, out var id))
        {
            id = ids.FirstOrDefault(pair => pair.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).Value;
        }
        if (id == 0)
        {
            throw new ContentStudioException($"Missing localized_texts ID for '{key}'.");
        }
        return id;
    }

    private static string ToDatabaseBoolean(bool value) => value ? "t" : "f";
}
