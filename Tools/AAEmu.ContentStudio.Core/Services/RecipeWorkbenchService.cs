using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

/// <summary>
/// Keeps a recipe's required crafting object and crafting-menu membership together.
/// ArcheAge lists recipes through craft packs; req_doodad_id alone does not move a
/// recipe to the selected workbench's menu.
/// </summary>
public sealed class RecipeWorkbenchService
{
    public RecipeWorkbenchAssignment Assign(
        RecipeDefinition recipe,
        string baselinePath,
        string projectPath,
        uint doodadId)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (doodadId == 0)
        {
            recipe.RequiredDoodadId = 0;
            return new RecipeWorkbenchAssignment(0, "No additional workbench requirement", recipe.CraftPackIds);
        }

        var project = new ProjectRepository().LoadProject(projectPath);
        var customWorkbench = project.Workbenches.FirstOrDefault(workbench => workbench.Id == doodadId);
        if (customWorkbench is not null)
        {
            if (customWorkbench.CraftPack.Id == 0)
            {
                throw new ContentStudioException($"{DisplayName(customWorkbench)} does not have a usable crafting menu.");
            }

            return Apply(recipe, doodadId, DisplayName(customWorkbench), [customWorkbench.CraftPack.Id]);
        }

        var workbench = new CompactCatalogService().GetWorkbench(baselinePath, doodadId)
            ?? throw new ContentStudioException("That crafting object could not be loaded. Choose a workbench from the list.");
        if (workbench.CraftPackIds.Count == 0)
        {
            throw new ContentStudioException($"{workbench.Name} does not offer a crafting menu. Choose a different workbench.");
        }

        return Apply(recipe, doodadId, workbench.Name, workbench.CraftPackIds);
    }

    private static RecipeWorkbenchAssignment Apply(
        RecipeDefinition recipe,
        uint doodadId,
        string name,
        IEnumerable<uint> craftPackIds)
    {
        var packs = craftPackIds.Distinct().Order().ToArray();
        recipe.RequiredDoodadId = doodadId;
        recipe.CraftPackIds = packs;
        return new RecipeWorkbenchAssignment(doodadId, name, packs);
    }

    private static string DisplayName(WorkbenchDefinition workbench)
        => workbench.Names.GetValueOrDefault("en_us", workbench.Key);
}

public sealed record RecipeWorkbenchAssignment(uint DoodadId, string WorkbenchName, uint[] CraftPackIds);
