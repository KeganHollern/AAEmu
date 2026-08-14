using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class ChangeDeletionService
{
    private readonly ManifestService _manifests = new();
    private readonly ProjectRepository _repository = new();

    public ChangeDeletionPreview Preview(string projectPath, string manifestPath)
    {
        var project = _repository.LoadProject(projectPath);
        var path = RequireManagedManifest(projectPath, manifestPath);
        var identity = ReadIdentity(path);
        var preview = new ChangeDeletionPreview
        {
            Path = path,
            Key = identity.Key,
            Name = identity.Name,
            Type = identity.Type,
            RetiredIdCount = CountOwnedAllocations(project.Registry, identity.Key)
        };

        if (identity.Recipe is not null)
        {
            var workbenches = project.Workbenches.Where(workbench => workbench.RecipeIds.Contains(identity.Recipe.Id)).ToList();
            preview.Consequences.Add(workbenches.Count == 0
                ? "No saved workbench uses this recipe."
                : $"It will be removed from {workbenches.Count} saved workbench{(workbenches.Count == 1 ? string.Empty : "es")}.");
            preview.Blockers.AddRange(FindRecordReferences(project, "crafts", identity.Recipe.Id));
        }
        else if (identity.Workbench is not null)
        {
            var recipes = project.Recipes.Where(recipe => recipe.RequiredDoodadId == identity.Workbench.Id || recipe.CraftPackIds.Contains(identity.Workbench.CraftPack.Id)).ToList();
            preview.Consequences.Add(recipes.Count == 0
                ? "No saved recipe depends on this workbench."
                : $"{recipes.Count} saved recipe{(recipes.Count == 1 ? "" : "s")} will be kept and changed to require no workbench.");
            preview.Blockers.AddRange(FindRecordReferences(project, "doodad_almighties", identity.Workbench.Id));
        }
        else if (identity.Record is not null)
        {
            if (identity.Record.Mode == RecordChangeMode.Modify)
            {
                preview.Consequences.Add("The original game entry will return to its unchanged baseline behavior.");
            }
            else
            {
                preview.Consequences.Add("Its custom ID and all copied child-row IDs will be permanently retired.");
                preview.Blockers.AddRange(FindReferences(project, identity.Record));
            }
        }
        else if (identity.Assertion is not null)
        {
            preview.Consequences.Add("The build will no longer enforce this release requirement.");
        }

        if (preview.RetiredIdCount > 0)
        {
            preview.Consequences.Add($"{preview.RetiredIdCount} allocated ID{(preview.RetiredIdCount == 1 ? "" : "s")} will be moved to tombstones and never reused.");
        }
        preview.Consequences.Add("The compiled database and live game are not changed until the project is built and deployed again.");
        preview.Blockers = preview.Blockers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return preview;
    }

    public ChangeDeletionResult Delete(string projectPath, string manifestPath)
    {
        var preview = Preview(projectPath, manifestPath);
        if (!preview.CanDelete)
        {
            throw new ContentStudioException($"This change cannot be deleted yet: {string.Join(" ", preview.Blockers)}");
        }

        var project = _repository.LoadProject(projectPath);
        var identity = ReadIdentity(preview.Path);
        var updatedChanges = 0;
        var additionallyRetired = new HashSet<uint>();

        if (identity.Recipe is not null)
        {
            foreach (var path in ManifestPaths(project, "workbenches"))
            {
                var workbench = ContentStudioJson.Deserialize<WorkbenchDefinition>(File.ReadAllText(path), path);
                var keptRecipes = new List<uint>();
                var keptLinks = new List<uint>();
                var changed = false;
                for (var index = 0; index < workbench.RecipeIds.Length; index++)
                {
                    if (workbench.RecipeIds[index] == identity.Recipe.Id)
                    {
                        changed = true;
                        if (index < workbench.RowIds.CraftPackLinks.Length) additionallyRetired.Add(workbench.RowIds.CraftPackLinks[index]);
                        continue;
                    }
                    keptRecipes.Add(workbench.RecipeIds[index]);
                    if (index < workbench.RowIds.CraftPackLinks.Length) keptLinks.Add(workbench.RowIds.CraftPackLinks[index]);
                }
                if (!changed) continue;
                workbench.RecipeIds = [.. keptRecipes];
                workbench.RowIds.CraftPackLinks = [.. keptLinks];
                _manifests.Save(path, ContentStudioJson.Serialize(workbench));
                updatedChanges++;
            }
        }
        else if (identity.Workbench is not null)
        {
            foreach (var path in ManifestPaths(project, "recipes"))
            {
                var recipe = ContentStudioJson.Deserialize<RecipeDefinition>(File.ReadAllText(path), path);
                var changed = false;
                if (recipe.RequiredDoodadId == identity.Workbench.Id)
                {
                    recipe.RequiredDoodadId = 0;
                    changed = true;
                }
                for (var index = recipe.CraftPackIds.Length - 1; index >= 0; index--)
                {
                    if (recipe.CraftPackIds[index] != identity.Workbench.CraftPack.Id) continue;
                    if (index < recipe.RowIds.CraftPackLinks.Length) additionallyRetired.Add(recipe.RowIds.CraftPackLinks[index]);
                    recipe.CraftPackIds = recipe.CraftPackIds.Where((_, itemIndex) => itemIndex != index).ToArray();
                    recipe.RowIds.CraftPackLinks = recipe.RowIds.CraftPackLinks.Where((_, itemIndex) => itemIndex != index).ToArray();
                    changed = true;
                }
                if (!changed) continue;
                _manifests.Save(path, ContentStudioJson.Serialize(recipe));
                updatedChanges++;
            }
        }

        File.Delete(preview.Path);
        var retired = RetireOwnedAllocations(project.Registry, identity.Key);
        retired += RetireAllocationIds(project.Registry, "craft_pack_crafts", additionallyRetired);
        _repository.SaveRegistry(projectPath, project.Registry);
        return new ChangeDeletionResult { Name = identity.Name, Type = identity.Type, RetiredIdCount = retired, UpdatedChangeCount = updatedChanges };
    }

    private string RequireManagedManifest(string projectPath, string manifestPath)
    {
        var requested = Path.GetFullPath(manifestPath);
        var match = _manifests.List(projectPath).FirstOrDefault(path => Path.GetFullPath(path).Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (match is null || Path.GetFileName(match).Equals("project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStudioException("Only a saved recipe, workbench, entry, or release check can be deleted here.");
        }
        return Path.GetFullPath(match);
    }

    private static ChangeIdentity ReadIdentity(string path)
    {
        var json = File.ReadAllText(path);
        if (IsInFolder(path, "recipes"))
        {
            var value = ContentStudioJson.Deserialize<RecipeDefinition>(json, path);
            return new ChangeIdentity(value.Key, value.Names.GetValueOrDefault("en_us", value.Key), "Recipe") { Recipe = value };
        }
        if (IsInFolder(path, "workbenches"))
        {
            var value = ContentStudioJson.Deserialize<WorkbenchDefinition>(json, path);
            return new ChangeIdentity(value.Key, value.Names.GetValueOrDefault("en_us", value.Key), "Workbench") { Workbench = value };
        }
        if (IsInFolder(path, "records"))
        {
            var value = ContentStudioJson.Deserialize<RecordDefinition>(json, path);
            return new ChangeIdentity(value.Key, value.DisplayName, CatalogRecordService.FriendlyName(value.Table.TrimEnd('s'))) { Record = value };
        }
        if (IsInFolder(path, "assertions"))
        {
            var value = ContentStudioJson.Deserialize<ContentAssertionDefinition>(json, path);
            return new ChangeIdentity(value.Key, value.Description, "Release check") { Assertion = value };
        }
        throw new ContentStudioException("This file is not a supported saved change.");
    }

    private static IEnumerable<string> ManifestPaths(LoadedContentProject project, string folder) =>
        project.SourceFiles.Where(path => IsInFolder(path, folder) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    private static bool IsInFolder(string path, string folder) =>
        path.Contains($"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static int CountOwnedAllocations(IdRegistry registry, string key) => registry.Allocations.Values.Sum(values => values.Count(pair => IsOwned(pair.Key, key)));

    private static int RetireOwnedAllocations(IdRegistry registry, string key)
    {
        var count = 0;
        foreach (var table in registry.Allocations.Keys.ToList())
        {
            var allocations = registry.Allocations[table];
            foreach (var allocation in allocations.Where(pair => IsOwned(pair.Key, key)).ToList())
            {
                TombstonesFor(registry, table)[allocation.Key] = allocation.Value;
                allocations.Remove(allocation.Key);
                count++;
            }
        }
        return count;
    }

    private static int RetireAllocationIds(IdRegistry registry, string table, HashSet<uint> ids)
    {
        if (ids.Count == 0 || !registry.Allocations.TryGetValue(table, out var allocations)) return 0;
        var count = 0;
        foreach (var allocation in allocations.Where(pair => ids.Contains(pair.Value)).ToList())
        {
            TombstonesFor(registry, table)[allocation.Key] = allocation.Value;
            allocations.Remove(allocation.Key);
            count++;
        }
        return count;
    }

    private static Dictionary<string, uint> TombstonesFor(IdRegistry registry, string table)
    {
        if (!registry.Tombstones.TryGetValue(table, out var values))
        {
            values = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            registry.Tombstones[table] = values;
        }
        return values;
    }

    private static bool IsOwned(string allocationKey, string changeKey) => allocationKey.Equals(changeKey, StringComparison.OrdinalIgnoreCase) || allocationKey.StartsWith(changeKey + ":", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> FindReferences(LoadedContentProject project, RecordDefinition target)
    {
        if (target.Table.Equals("items", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var recipe in project.Recipes.Where(recipe => recipe.Materials.Any(row => row.ItemId == target.Id) || recipe.Products.Any(row => row.ItemId == target.Id)))
                yield return $"Recipe '{recipe.Names.GetValueOrDefault("en_us", recipe.Key)}' still uses this item.";
        }
        if (target.Table.Equals("skills", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var recipe in project.Recipes.Where(recipe => recipe.SkillId == target.Id))
                yield return $"Recipe '{recipe.Names.GetValueOrDefault("en_us", recipe.Key)}' still uses this skill.";
        }
        if (target.Table.Equals("crafts", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var workbench in project.Workbenches.Where(workbench => workbench.RecipeIds.Contains(target.Id)))
                yield return $"Workbench '{workbench.Names.GetValueOrDefault("en_us", workbench.Key)}' still uses this recipe.";
        }
        if (target.Table.Equals("doodad_almighties", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var recipe in project.Recipes.Where(recipe => recipe.RequiredDoodadId == target.Id))
                yield return $"Recipe '{recipe.Names.GetValueOrDefault("en_us", recipe.Key)}' still requires this world object.";
        }
        foreach (var reference in FindRecordReferences(project, target.Table, target.Id, target.Key)) yield return reference;
    }

    private static IEnumerable<string> FindRecordReferences(LoadedContentProject project, string targetTable, uint targetId, string? excludedKey = null)
    {
        foreach (var record in project.Records.Where(record => !record.Key.Equals(excludedKey, StringComparison.OrdinalIgnoreCase)))
        {
            var found = record.Values.Any(field => IsReference(field.Key, field.Value, targetTable, targetId)) ||
                        record.Children.Any(child => child.Values.Any(field => IsReference(field.Key, field.Value, targetTable, targetId))) ||
                        record.LinkedClones.Any(linked => linked.Values.Any(field => IsReference(field.Key, field.Value, targetTable, targetId)));
            if (found) yield return $"'{record.DisplayName}' still links to this entry.";
        }
    }

    private static bool IsReference(string fieldName, string? value, string targetTable, uint targetId) =>
        uint.TryParse(value, out var parsed) && parsed == targetId && string.Equals(CatalogRecordService.ReferenceTableFor(fieldName), targetTable, StringComparison.OrdinalIgnoreCase);

    private sealed record ChangeIdentity(string Key, string Name, string Type)
    {
        public RecipeDefinition? Recipe { get; init; }
        public WorkbenchDefinition? Workbench { get; init; }
        public RecordDefinition? Record { get; init; }
        public ContentAssertionDefinition? Assertion { get; init; }
    }
}
