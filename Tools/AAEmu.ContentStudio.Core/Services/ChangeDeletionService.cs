using System.Text;
using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class ChangeDeletionService
{
    private readonly ManifestService _manifests = new();
    private readonly ProjectRepository _repository = new();
    private readonly Action<int, string>? _beforeApply;

    public ChangeDeletionService()
    {
    }

    internal ChangeDeletionService(Action<int, string> beforeApply)
    {
        _beforeApply = beforeApply;
    }

    public ChangeDeletionPreview Preview(string projectPath, string manifestPath)
    {
        var project = _repository.LoadProject(projectPath);
        var path = RequireManagedManifest(projectPath, manifestPath);
        var snapshot = _manifests.ReadSnapshot(path);
        var identity = ReadIdentity(path, snapshot.Contents);
        var preview = new ChangeDeletionPreview
        {
            Path = path,
            Version = snapshot.Version,
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

    public ChangeDeletionResult Delete(string projectPath, string manifestPath, string expectedVersion)
    {
        lock (AtomicFile.SyncRoot)
        {
            return DeleteCore(projectPath, manifestPath, expectedVersion);
        }
    }

    private ChangeDeletionResult DeleteCore(string projectPath, string manifestPath, string expectedVersion)
    {
        var preview = Preview(projectPath, manifestPath);
        if (!preview.Version.Equals(expectedVersion, StringComparison.Ordinal))
        {
            throw new ContentStudioException("This saved change was updated outside this editor. Reload it to see the newest work before deleting it.");
        }
        if (!preview.CanDelete)
        {
            throw new ContentStudioException($"This change cannot be deleted yet: {string.Join(" ", preview.Blockers)}");
        }

        _beforeApply?.Invoke(-1, preview.Path);
        var discoveredProject = _repository.LoadProject(projectPath);
        var sourceContents = discoveredProject.SourceFiles.ToDictionary(Path.GetFullPath, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
        var project = _repository.LoadProject(projectPath, sourceContents);
        EnsureSourceSet(project.SourceFiles, sourceContents.Keys);
        var targetContents = sourceContents[preview.Path];
        if (!ManifestService.Fingerprint(targetContents).Equals(preview.Version, StringComparison.Ordinal))
        {
            throw new ContentStudioException("This saved change was updated outside this editor. Reload it to see the newest work before deleting it.");
        }
        var identity = ReadIdentity(preview.Path, targetContents);
        var stableBlockers = FindBlockers(project, identity).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (stableBlockers.Count > 0)
        {
            throw new ContentStudioException($"This change cannot be deleted yet: {string.Join(" ", stableBlockers)}");
        }
        var registryPath = Path.GetFullPath(Path.Combine(project.ProjectDirectory, project.Definition.IdRegistry));
        var registryContents = sourceContents[registryPath];
        var registry = ContentStudioJson.Deserialize<IdRegistry>(registryContents, registryPath);
        IdRegistryService.NormalizeComparers(registry);
        var mutations = new List<FileMutation>();
        var updatedChanges = 0;
        var additionallyRetired = new HashSet<uint>();

        if (identity.Recipe is not null)
        {
            foreach (var path in ManifestPaths(project, "workbenches"))
            {
                var original = sourceContents[path];
                var workbench = ContentStudioJson.Deserialize<WorkbenchDefinition>(original, path);
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
                mutations.Add(new FileMutation(path, original, Normalize(ContentStudioJson.Serialize(workbench))));
                updatedChanges++;
            }
        }
        else if (identity.Workbench is not null)
        {
            foreach (var path in ManifestPaths(project, "recipes"))
            {
                var original = sourceContents[path];
                var recipe = ContentStudioJson.Deserialize<RecipeDefinition>(original, path);
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
                mutations.Add(new FileMutation(path, original, Normalize(ContentStudioJson.Serialize(recipe))));
                updatedChanges++;
            }
        }

        mutations.Add(new FileMutation(preview.Path, targetContents, null));
        var retired = RetireOwnedAllocations(registry, identity.Key);
        retired += RetireAllocationIds(registry, "craft_pack_crafts", additionallyRetired);
        var updatedRegistry = Normalize(ContentStudioJson.Serialize(registry));
        if (!updatedRegistry.Equals(registryContents, StringComparison.Ordinal))
        {
            mutations.Add(new FileMutation(registryPath, registryContents, updatedRegistry));
        }
        ApplyMutations(projectPath, sourceContents, mutations, identity);
        return new ChangeDeletionResult { Name = identity.Name, Type = identity.Type, RetiredIdCount = retired, UpdatedChangeCount = updatedChanges };
    }

    private void ApplyMutations(
        string projectPath,
        IReadOnlyDictionary<string, string> sourceContents,
        IReadOnlyList<FileMutation> mutations,
        ChangeIdentity identity)
    {
        var stagedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var mutation in mutations.Where(value => value.Replacement is not null))
            {
                var directory = Path.GetDirectoryName(mutation.Path)
                    ?? throw new ContentStudioException($"Unable to determine the project directory for {mutation.Path}.");
                var stagingPath = Path.Combine(directory, $".{Path.GetFileName(mutation.Path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(stagingPath, mutation.Replacement!, new UTF8Encoding(false));
                stagedPaths[mutation.Path] = stagingPath;
            }

            foreach (var mutation in mutations)
            {
                EnsureUnchanged(mutation);
            }
            EnsureProjectUnchanged(projectPath, sourceContents);

            var applied = new List<FileMutation>();
            try
            {
                for (var index = 0; index < mutations.Count; index++)
                {
                    var mutation = mutations[index];
                    _beforeApply?.Invoke(index, mutation.Path);
                    if (index == 0) EnsureProjectUnchanged(projectPath, sourceContents);
                    else EnsureUntouchedSources(sourceContents, mutations);
                    EnsureUnchanged(mutation);
                    if (mutation.Replacement is null)
                    {
                        File.Delete(mutation.Path);
                    }
                    else
                    {
                        File.Move(stagedPaths[mutation.Path], mutation.Path, true);
                    }
                    applied.Add(mutation);
                }
                EnsureFinalProjectState(projectPath, sourceContents, mutations, identity);
            }
            catch (Exception exception)
            {
                Exception? rollbackFailure = null;
                foreach (var mutation in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        EnsureStillApplied(mutation);
                        AtomicFile.WriteAllText(mutation.Path, mutation.Original);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackFailure ??= rollbackException;
                    }
                }

                throw rollbackFailure is null
                    ? new ContentStudioException("The deletion could not be completed. All project files were restored.", exception)
                    : new ContentStudioException("The deletion failed and at least one project file could not be restored. Stop editing and recover the project from version control.", new AggregateException(exception, rollbackFailure));
            }
        }
        finally
        {
            foreach (var stagingPath in stagedPaths.Values)
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
            }
        }
    }

    private static void EnsureUnchanged(FileMutation mutation)
    {
        if (!File.Exists(mutation.Path) || !File.ReadAllText(mutation.Path).Equals(mutation.Original, StringComparison.Ordinal))
        {
            throw new ContentStudioException("A saved change or the ID registry was updated while deletion was being prepared. Reload My changes and review the deletion again.");
        }
    }

    private static void EnsureStillApplied(FileMutation mutation)
    {
        if (mutation.Replacement is null)
        {
            if (File.Exists(mutation.Path))
                throw new ContentStudioException($"Cannot safely restore '{mutation.Path}' because another process recreated it after this deletion removed it.");
            return;
        }
        if (!File.Exists(mutation.Path) || !File.ReadAllText(mutation.Path).Equals(mutation.Replacement, StringComparison.Ordinal))
        {
            throw new ContentStudioException($"Cannot safely restore '{mutation.Path}' because another process changed it after this deletion wrote it.");
        }
    }

    private void EnsureFinalProjectState(
        string projectPath,
        IReadOnlyDictionary<string, string> sourceContents,
        IReadOnlyList<FileMutation> mutations,
        ChangeIdentity identity)
    {
        LoadedContentProject current;
        try
        {
            current = _repository.LoadProject(projectPath);
        }
        catch (Exception exception)
        {
            throw new ContentStudioException("Project sources changed while deletion was being applied. All project files will be restored.", exception);
        }

        var expectedPaths = sourceContents.Keys.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in mutations)
        {
            if (mutation.Replacement is null) expectedPaths.Remove(Path.GetFullPath(mutation.Path));
            else expectedPaths.Add(Path.GetFullPath(mutation.Path));
        }
        EnsureSourceSet(current.SourceFiles, expectedPaths);

        var replacements = mutations.ToDictionary(mutation => Path.GetFullPath(mutation.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, original) in sourceContents)
        {
            if (replacements.TryGetValue(Path.GetFullPath(path), out var mutation))
            {
                if (mutation.Replacement is null)
                {
                    if (File.Exists(path))
                        throw new ContentStudioException("A deleted project source was recreated while deletion was being applied. All project files will be restored.");
                }
                else if (!File.Exists(path) || !File.ReadAllText(path).Equals(mutation.Replacement, StringComparison.Ordinal))
                {
                    throw new ContentStudioException("A project source changed while deletion was being applied. All project files will be restored.");
                }
            }
            else if (!File.Exists(path) || !File.ReadAllText(path).Equals(original, StringComparison.Ordinal))
            {
                throw new ContentStudioException("An unrelated project source changed while deletion was being applied. All project files will be restored.");
            }
        }

        var residualDependencies = FindPostDeletionDependencies(current, identity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (residualDependencies.Count > 0)
        {
            throw new ContentStudioException($"A project source added a new dependency while deletion was being applied: {string.Join(" ", residualDependencies)}");
        }
    }

    private static string Normalize(string json) => json.TrimEnd() + Environment.NewLine;

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

    private static ChangeIdentity ReadIdentity(string path, string json)
    {
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

    private static IEnumerable<string> FindBlockers(LoadedContentProject project, ChangeIdentity identity)
    {
        if (identity.Recipe is not null)
            return FindRecordReferences(project, "crafts", identity.Recipe.Id);
        if (identity.Workbench is not null)
            return FindRecordReferences(project, "doodad_almighties", identity.Workbench.Id);
        if (identity.Record is not null && identity.Record.Mode == RecordChangeMode.Duplicate)
            return FindReferences(project, identity.Record);
        return [];
    }

    private static IEnumerable<string> FindPostDeletionDependencies(LoadedContentProject project, ChangeIdentity identity)
    {
        if (identity.Recipe is not null)
        {
            foreach (var workbench in project.Workbenches.Where(value => value.RecipeIds.Contains(identity.Recipe.Id)))
                yield return $"Workbench '{workbench.Key}' still uses recipe {identity.Recipe.Id}.";
            foreach (var reference in FindRecordReferences(project, "crafts", identity.Recipe.Id)) yield return reference;
            yield break;
        }
        if (identity.Workbench is not null)
        {
            foreach (var recipe in project.Recipes.Where(value =>
                         value.RequiredDoodadId == identity.Workbench.Id ||
                         value.CraftPackIds.Contains(identity.Workbench.CraftPack.Id)))
                yield return $"Recipe '{recipe.Key}' still uses workbench {identity.Workbench.Id}.";
            foreach (var reference in FindRecordReferences(project, "doodad_almighties", identity.Workbench.Id)) yield return reference;
            yield break;
        }
        if (identity.Record is not null && identity.Record.Mode == RecordChangeMode.Duplicate)
        {
            foreach (var reference in FindReferences(project, identity.Record)) yield return reference;
        }
    }

    private void EnsureProjectUnchanged(string projectPath, IReadOnlyDictionary<string, string> sourceContents)
    {
        LoadedContentProject current;
        try
        {
            current = _repository.LoadProject(projectPath);
        }
        catch (Exception exception)
        {
            throw new ContentStudioException("Project sources changed while deletion was being prepared. Reload My changes and review the deletion again.", exception);
        }
        EnsureSourceSet(current.SourceFiles, sourceContents.Keys);
        foreach (var (path, contents) in sourceContents)
        {
            if (!File.Exists(path) || !File.ReadAllText(path).Equals(contents, StringComparison.Ordinal))
            {
                throw new ContentStudioException("Project sources changed while deletion was being prepared. Reload My changes and review the deletion again.");
            }
        }
    }

    private static void EnsureSourceSet(IEnumerable<string> currentPaths, IEnumerable<string> expectedPaths)
    {
        var current = currentPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = expectedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!current.SetEquals(expected))
        {
            throw new ContentStudioException("Project source files changed while deletion was being prepared. Reload My changes and review the deletion again.");
        }
    }

    private static void EnsureUntouchedSources(
        IReadOnlyDictionary<string, string> sourceContents,
        IReadOnlyList<FileMutation> mutations)
    {
        var changedPaths = mutations.Select(mutation => mutation.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, contents) in sourceContents.Where(pair => !changedPaths.Contains(pair.Key)))
        {
            if (!File.Exists(path) || !File.ReadAllText(path).Equals(contents, StringComparison.Ordinal))
            {
                throw new ContentStudioException("An unrelated project source changed while deletion was being applied. All project files will be restored.");
            }
        }
    }

    private static bool IsInFolder(string path, string folder) =>
        path.Contains($"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static int CountOwnedAllocations(IdRegistry registry, string key) => registry.Allocations.Values.Sum(values => values.Count(pair => IsOwned(pair.Key, key)));

    private static int RetireOwnedAllocations(IdRegistry registry, string key)
    {
        var count = 0;
        foreach (var table in registry.Allocations.Keys.ToList())
        {
            var allocations = registry.Allocations[table];
            var retired = allocations.Where(pair => IsOwned(pair.Key, key)).ToList();
            foreach (var allocation in retired)
            {
                allocations.Remove(allocation.Key);
                count++;
            }
            foreach (var allocation in retired)
                IdRegistryService.AddTombstone(registry, table, allocation.Key, allocation.Value);
        }
        return count;
    }

    private static int RetireAllocationIds(IdRegistry registry, string table, HashSet<uint> ids)
    {
        if (ids.Count == 0 || !registry.Allocations.TryGetValue(table, out var allocations)) return 0;
        var count = 0;
        var retired = allocations.Where(pair => ids.Contains(pair.Value)).ToList();
        foreach (var allocation in retired)
        {
            allocations.Remove(allocation.Key);
            count++;
        }
        foreach (var allocation in retired)
            IdRegistryService.AddTombstone(registry, table, allocation.Key, allocation.Value);
        return count;
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

    private sealed record FileMutation(string Path, string Original, string? Replacement);
}
