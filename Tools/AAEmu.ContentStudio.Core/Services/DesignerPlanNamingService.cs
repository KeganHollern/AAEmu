using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class DesignerPlanNamingService
{
    private readonly ProjectRepository _repository = new();

    public DesignerPlanSuggestion SuggestRecipeCopy(string projectPath, string sourceName)
    {
        var project = _repository.LoadProject(projectPath);
        return SuggestCopy(
            $"Custom {sourceName.Trim()}",
            $"recipe.custom-{Slug(sourceName)}",
            project.Recipes.Select(recipe => (recipe.Key, DisplayName(recipe))));
    }

    public DesignerPlanSuggestion SuggestWorkbenchCopy(string projectPath, string sourceName)
    {
        var project = _repository.LoadProject(projectPath);
        return SuggestCopy(
            $"Custom {sourceName.Trim()}",
            $"workbench.custom-{Slug(sourceName)}",
            project.Workbenches.Select(workbench => (workbench.Key, DisplayName(workbench))));
    }

    public bool RecipeNameExists(string projectPath, string name)
    {
        var normalized = name.Trim();
        return _repository.LoadProject(projectPath).Recipes.Any(recipe => DisplayName(recipe).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public bool WorkbenchNameExists(string projectPath, string name)
    {
        var normalized = name.Trim();
        return _repository.LoadProject(projectPath).Workbenches.Any(workbench => DisplayName(workbench).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public string SuggestRecipeName(string projectPath, string preferredName) =>
        SuggestName(preferredName, _repository.LoadProject(projectPath).Recipes.Select(DisplayName));

    public string SuggestWorkbenchName(string projectPath, string preferredName) =>
        SuggestName(preferredName, _repository.LoadProject(projectPath).Workbenches.Select(DisplayName));

    public string EnsureUniqueRecipeKey(string projectPath, string preferredKey) =>
        EnsureUniqueKey(preferredKey, _repository.LoadProject(projectPath).Recipes.Select(recipe => recipe.Key));

    public string EnsureUniqueWorkbenchKey(string projectPath, string preferredKey) =>
        EnsureUniqueKey(preferredKey, _repository.LoadProject(projectPath).Workbenches.Select(workbench => workbench.Key));

    private static DesignerPlanSuggestion SuggestCopy(string preferredName, string preferredKey, IEnumerable<(string Key, string Name)> existing)
    {
        var used = existing.ToList();
        for (var copyNumber = 1; ; copyNumber++)
        {
            var suffix = copyNumber == 1 ? string.Empty : $" {copyNumber}";
            var keySuffix = copyNumber == 1 ? string.Empty : $"-{copyNumber}";
            var name = preferredName + suffix;
            var key = preferredKey + keySuffix;
            if (used.All(item => !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                return new DesignerPlanSuggestion(name, key);
            }
        }
    }

    private static string SuggestName(string preferredName, IEnumerable<string> existingNames)
    {
        var trimmed = preferredName.Trim();
        var used = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(trimmed)) return trimmed;
        for (var copyNumber = 2; ; copyNumber++)
        {
            var candidate = $"{trimmed} {copyNumber}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static string EnsureUniqueKey(string preferredKey, IEnumerable<string> existingKeys)
    {
        var trimmed = preferredKey.Trim();
        var used = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(trimmed)) return trimmed;
        for (var copyNumber = 2; ; copyNumber++)
        {
            var candidate = $"{trimmed}-{copyNumber}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static string DisplayName(RecipeDefinition recipe) => recipe.Names.GetValueOrDefault("en_us", recipe.Key).Trim();
    private static string DisplayName(WorkbenchDefinition workbench) => workbench.Names.GetValueOrDefault("en_us", workbench.Key).Trim();

    private static string Slug(string value)
    {
        var characters = value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var slug = string.Join('-', new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "new-copy" : slug;
    }
}

public sealed record DesignerPlanSuggestion(string Name, string Key);
