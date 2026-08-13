using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class ManifestService
{
    public IReadOnlyList<string> List(string projectPath)
    {
        var project = new ProjectRepository().LoadProject(projectPath);
        return project.SourceFiles
            .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals("id-registry.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Read(string path) => File.ReadAllText(RequireProjectManifest(path));

    public void Save(string path, string json)
    {
        var fullPath = RequireProjectManifest(path);
        var fileName = Path.GetFileName(fullPath);
        if (fileName.Equals("project.json", StringComparison.OrdinalIgnoreCase))
        {
            _ = ContentStudioJson.Deserialize<ContentProjectDefinition>(json, fullPath);
        }
        else if (fullPath.Contains($"{Path.DirectorySeparatorChar}recipes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            _ = ContentStudioJson.Deserialize<RecipeDefinition>(json, fullPath);
        }
        else if (fullPath.Contains($"{Path.DirectorySeparatorChar}workbenches{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            _ = ContentStudioJson.Deserialize<WorkbenchDefinition>(json, fullPath);
        }
        else
        {
            throw new ContentStudioException("Only the project, recipe, and workbench JSON manifests are editable here.");
        }
        AtomicFile.WriteAllText(fullPath, json.TrimEnd() + Environment.NewLine);
    }

    private static string RequireProjectManifest(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStudioException($"Manifest does not exist: {fullPath}");
        }
        return fullPath;
    }
}
