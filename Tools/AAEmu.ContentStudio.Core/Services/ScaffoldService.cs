using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class ScaffoldService
{
    private readonly ProjectRepository _repository = new();
    private readonly IdRegistryService _ids = new();
    private readonly CompactCatalogService _catalog = new();

    public RecipeDefinition CreateRecipeDraft(string baselinePath, uint sourceRecipeId)
    {
        var graph = _catalog.GetRecipe(baselinePath, sourceRecipeId)
            ?? throw new ContentStudioException($"Source recipe {sourceRecipeId} does not exist.");
        var recipe = ReadRecipeTemplate(baselinePath, sourceRecipeId);
        recipe.Names = new Dictionary<string, string> { ["en_us"] = $"Custom {graph.Name}" };
        if (!string.IsNullOrWhiteSpace(graph.Description))
        {
            recipe.Descriptions["en_us"] = graph.Description;
        }
        recipe.CraftPackIds = graph.CraftPackIds.ToArray();
        recipe.Materials = graph.Materials.Select(material => new RecipeMaterialDefinition
        {
            ItemId = material.ItemId,
            Amount = material.Amount,
            MainGrade = material.MainGrade,
            RequiredGrade = material.RequiredGrade
        }).ToList();
        recipe.Products = graph.Products.Select(product => new RecipeProductDefinition
        {
            ItemId = product.ItemId,
            Amount = product.Amount,
            Rate = product.Rate,
            ShowLowerCrafts = product.ShowLowerCrafts,
            UseGrade = product.UseGrade,
            ItemGradeId = product.ItemGradeId
        }).ToList();
        return recipe;
    }

    public ScaffoldResult CreateRecipe(RecipeScaffoldRequest request)
    {
        var key = NormalizeKey(request.Key, "recipe");
        var project = _repository.LoadProject(request.ProjectPath);
        var graph = _catalog.GetRecipe(request.BaselinePath, request.SourceRecipeId)
            ?? throw new ContentStudioException($"Source recipe {request.SourceRecipeId} does not exist.");
        var allocations = new List<IdAllocation>();
        IdAllocation Allocate(string table, string suffix)
        {
            var allocation = _ids.Allocate(project.Registry, request.BaselinePath, table, $"{key}:{suffix}");
            allocations.Add(allocation);
            return allocation;
        }

        var recipe = request.Draft ?? CreateRecipeDraft(request.BaselinePath, request.SourceRecipeId);
        recipe.Key = key;
        recipe.Id = Allocate("crafts", "row").Id;
        if (request.Draft is null)
        {
            recipe.Names["en_us"] = string.IsNullOrWhiteSpace(request.Name) ? $"Custom {graph.Name}" : request.Name;
            recipe.CraftPackIds = request.CraftPackIds ?? [];
        }
        else if (string.IsNullOrWhiteSpace(recipe.Names.GetValueOrDefault("en_us")))
        {
            recipe.Names["en_us"] = string.IsNullOrWhiteSpace(request.Name) ? $"Custom {graph.Name}" : request.Name;
        }
        recipe.RowIds = new RecipeRowIds();
        recipe.Materials = recipe.Materials.Select((material, index) => new RecipeMaterialDefinition
        {
            Id = Allocate("craft_materials", $"material:{index}").Id,
            ItemId = material.ItemId,
            Amount = material.Amount,
            MainGrade = material.MainGrade,
            RequiredGrade = material.RequiredGrade
        }).ToList();
        recipe.Products = recipe.Products.Select((product, index) => new RecipeProductDefinition
        {
            Id = Allocate("craft_products", $"product:{index}").Id,
            ItemId = product.ItemId,
            Amount = product.Amount,
            Rate = product.Rate,
            ShowLowerCrafts = product.ShowLowerCrafts,
            UseGrade = product.UseGrade,
            ItemGradeId = product.ItemGradeId
        }).ToList();
        recipe.RowIds.Localization["title"] = Allocate("localized_texts", "title").Id;
        if (recipe.Descriptions.Count > 0)
        {
            recipe.RowIds.Localization["desc"] = Allocate("localized_texts", "desc").Id;
        }
        recipe.RowIds.CraftPackLinks = recipe.CraftPackIds.Select((_, index) => Allocate("craft_pack_crafts", $"pack-link:{index}").Id).ToArray();

        if (request.CloneSkill)
        {
            var requestedSkill = recipe.SkillClone;
            var effectIds = ReadIds(request.BaselinePath, "SELECT id FROM skill_effects WHERE skill_id = @id ORDER BY id;", graph.SkillId);
            recipe.SkillClone = new SkillCloneDefinition
            {
                SourceId = graph.SkillId,
                Id = Allocate("skills", "skill").Id,
                LaborCost = requestedSkill?.LaborCost ?? graph.LaborCost,
                CastingTime = requestedSkill?.CastingTime ?? graph.CastingTime,
                SkillEffectRowIds = effectIds.Select((_, index) => Allocate("skill_effects", $"skill-effect:{index}").Id).ToArray()
            };
            recipe.SkillId = recipe.SkillClone.Id;
        }
        else
        {
            recipe.SkillClone = null;
            recipe.SkillId = graph.SkillId;
        }

        var directory = Path.Combine(project.ProjectDirectory, "recipes");
        var path = Path.Combine(directory, SanitizeFileName(key) + ".json");
        if (File.Exists(path))
        {
            throw new ContentStudioException("This recipe copy conflicts with another saved recipe. Choose a different name and try again.");
        }
        if (!request.DryRun)
        {
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(path, ContentStudioJson.Serialize(recipe) + Environment.NewLine);
            _repository.SaveRegistry(request.ProjectPath, project.Registry);
        }
        return new ScaffoldResult(path, recipe.Key, recipe.Id, allocations) { DryRun = request.DryRun };
    }

    public ScaffoldResult CreateWorkbench(WorkbenchScaffoldRequest request)
    {
        var key = NormalizeKey(request.Key, "workbench");
        var project = _repository.LoadProject(request.ProjectPath);
        var graph = _catalog.GetWorkbench(request.BaselinePath, request.SourceDoodadId)
            ?? throw new ContentStudioException($"Source doodad {request.SourceDoodadId} does not exist.");
        var allocations = new List<IdAllocation>();
        IdAllocation Allocate(string table, string suffix)
        {
            var allocation = _ids.Allocate(project.Registry, request.BaselinePath, table, $"{key}:{suffix}");
            allocations.Add(allocation);
            return allocation;
        }

        var workbench = new WorkbenchDefinition
        {
            Key = key,
            Id = Allocate("doodad_almighties", "row").Id,
            SourceDoodadId = request.SourceDoodadId,
            Names = new Dictionary<string, string> { ["en_us"] = string.IsNullOrWhiteSpace(request.Name) ? $"Custom {graph.Name}" : request.Name },
            ModelOverride = string.IsNullOrWhiteSpace(request.ModelOverride) ? null : request.ModelOverride.Trim(),
            CraftPack = new WorkbenchCraftPackDefinition
            {
                Id = Allocate("craft_packs", "craft-pack").Id,
                Name = string.IsNullOrWhiteSpace(request.CraftPackName) ? $"custom_{key.Replace('.', '_')}" : request.CraftPackName.Trim()
            },
            RecipeIds = request.RecipeIds,
            RowIds = new WorkbenchRowIds()
        };
        workbench.RowIds.Localization["name"] = Allocate("localized_texts", "name").Id;

        using var connection = CompactConnectionFactory.OpenReadOnly(request.BaselinePath);
        var groupIds = ReadIds(connection, "SELECT id FROM doodad_func_groups WHERE doodad_almighty_id = @id ORDER BY id;", request.SourceDoodadId);
        foreach (var groupId in groupIds)
        {
            workbench.RowIds.FunctionGroups[groupId] = Allocate("doodad_func_groups", $"group:{groupId}").Id;
            foreach (var function in ReadFunctionRows(connection, groupId))
            {
                workbench.RowIds.Functions[function.Id] = Allocate("doodad_funcs", $"func:{function.Id}").Id;
                if (function.Type.Equals("DoodadFuncCraftPack", StringComparison.Ordinal) && !workbench.RowIds.CraftPackPayloads.ContainsKey(function.ActualId))
                {
                    workbench.RowIds.CraftPackPayloads[function.ActualId] = Allocate("doodad_func_craft_packs", $"craft-pack-payload:{function.ActualId}").Id;
                }
            }
            foreach (var phaseId in ReadIds(connection, "SELECT id FROM doodad_phase_funcs WHERE doodad_func_group_id = @id ORDER BY id;", groupId))
            {
                workbench.RowIds.PhaseFunctions[phaseId] = Allocate("doodad_phase_funcs", $"phase-func:{phaseId}").Id;
            }
        }
        workbench.RowIds.CraftPackLinks = request.RecipeIds.Select((_, index) => Allocate("craft_pack_crafts", $"pack-link:{index}").Id).ToArray();

        var directory = Path.Combine(project.ProjectDirectory, "workbenches");
        var path = Path.Combine(directory, SanitizeFileName(key) + ".json");
        if (File.Exists(path))
        {
            throw new ContentStudioException("This workbench copy conflicts with another saved workbench. Choose a different name and try again.");
        }
        if (!request.DryRun)
        {
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(path, ContentStudioJson.Serialize(workbench) + Environment.NewLine);
            LinkCustomRecipesToWorkbench(project, workbench);
            _repository.SaveRegistry(request.ProjectPath, project.Registry);
        }
        return new ScaffoldResult(path, workbench.Key, workbench.Id, allocations)
        {
            DryRun = request.DryRun,
            GmCommand = $"/doodad spawn {workbench.Id}"
        };
    }

    private static void LinkCustomRecipesToWorkbench(LoadedContentProject project, WorkbenchDefinition workbench)
    {
        var recipeIds = workbench.RecipeIds.ToHashSet();
        foreach (var path in project.SourceFiles.Where(path => path.Contains($"{Path.DirectorySeparatorChar}recipes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            var recipe = ContentStudioJson.Deserialize<RecipeDefinition>(File.ReadAllText(path), path);
            if (!recipeIds.Contains(recipe.Id))
            {
                continue;
            }
            recipe.RequiredDoodadId = workbench.Id;
            recipe.CraftPackIds = [];
            recipe.RowIds.CraftPackLinks = [];
            AtomicFile.WriteAllText(path, ContentStudioJson.Serialize(recipe) + Environment.NewLine);
        }
    }

    private static RecipeDefinition ReadRecipeTemplate(string compactPath, uint id)
    {
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cast_delay, skill_id, wi_id, req_doodad_id, ac_id, actability_limit, recommend_level, visible_order, need_bind, show_upper_crafts FROM crafts WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new ContentStudioException($"Source recipe {id} does not exist.");
        }
        return new RecipeDefinition
        {
            CastDelay = reader.GetInt32(0),
            SkillId = Convert.ToUInt32(reader.GetInt64(1)),
            WorldInteractionId = Convert.ToUInt32(reader.GetInt64(2)),
            RequiredDoodadId = Convert.ToUInt32(reader.GetInt64(3)),
            ActabilityCategoryId = Convert.ToUInt32(reader.GetInt64(4)),
            ActabilityLimit = reader.GetInt32(5),
            RecommendLevel = reader.GetInt32(6),
            VisibleOrder = reader.GetInt32(7),
            NeedBind = ReadBoolean(reader, 8),
            ShowUpperCrafts = ReadBoolean(reader, 9)
        };
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

    private static List<uint> ReadIds(string compactPath, string sql, uint id)
    {
        using var connection = CompactConnectionFactory.OpenReadOnly(compactPath);
        return ReadIds(connection, sql, id);
    }

    private static List<uint> ReadIds(SqliteConnection connection, string sql, uint id)
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

    private static List<(uint Id, uint ActualId, string Type)> ReadFunctionRows(SqliteConnection connection, uint groupId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, actual_func_id, actual_func_type FROM doodad_funcs WHERE doodad_func_group_id = @id ORDER BY id;";
        command.Parameters.AddWithValue("@id", groupId);
        using var reader = command.ExecuteReader();
        var result = new List<(uint, uint, string)>();
        while (reader.Read())
        {
            result.Add((Convert.ToUInt32(reader.GetInt64(0)), Convert.ToUInt32(reader.GetInt64(1)), reader.GetString(2)));
        }
        return result;
    }

    private static string SanitizeFileName(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(key.Trim().ToLowerInvariant().Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray());
        if (value.Length == 0)
        {
            throw new ContentStudioException("The content key cannot produce an empty filename.");
        }
        return value;
    }

    private static string NormalizeKey(string key, string prefix)
    {
        var normalized = key.Trim().ToLowerInvariant().Replace(' ', '-');
        if (!normalized.StartsWith(prefix + ".", StringComparison.Ordinal))
        {
            normalized = prefix + "." + normalized;
        }
        if (normalized.Length == prefix.Length + 1 || normalized.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ContentStudioException($"Content key '{key}' contains unsupported characters.");
        }
        return normalized;
    }
}
