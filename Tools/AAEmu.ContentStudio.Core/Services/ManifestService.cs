using AAEmu.ContentStudio.Core.Models;
using System.Security.Cryptography;

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

    public string Read(string path) => ReadSnapshot(path).Contents;

    public ManifestSnapshot ReadSnapshot(string path)
    {
        var fullPath = RequireProjectManifest(path);
        var contents = File.ReadAllText(fullPath);
        return new ManifestSnapshot(fullPath, contents, Fingerprint(contents));
    }

    public string FindByKey(string projectPath, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ContentStudioException("Choose a saved change first.");
        }

        foreach (var path in List(projectPath))
        {
            var json = File.ReadAllText(path);
            var candidate = path.Contains($"{Path.DirectorySeparatorChar}recipes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? ContentStudioJson.Deserialize<RecipeDefinition>(json, path).Key
                : path.Contains($"{Path.DirectorySeparatorChar}workbenches{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    ? ContentStudioJson.Deserialize<WorkbenchDefinition>(json, path).Key
                    : path.Contains($"{Path.DirectorySeparatorChar}records{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        ? ContentStudioJson.Deserialize<RecordDefinition>(json, path).Key
                        : path.Contains($"{Path.DirectorySeparatorChar}assertions{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                            ? ContentStudioJson.Deserialize<ContentAssertionDefinition>(json, path).Key
                        : string.Empty;
            if (candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        throw new ContentStudioException($"Saved change '{key}' was not found in this project.");
    }

    public string Save(string path, string json, string? expectedVersion = null)
    {
        var fullPath = RequireProjectManifest(path);
        if (expectedVersion is not null)
        {
            var current = Fingerprint(File.ReadAllText(fullPath));
            if (!current.Equals(expectedVersion, StringComparison.Ordinal))
            {
                throw new ContentStudioException("This saved change was updated outside this editor. Reload it to see the newest work before saving your changes.");
            }
        }
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
        else if (fullPath.Contains($"{Path.DirectorySeparatorChar}records{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            _ = ContentStudioJson.Deserialize<RecordDefinition>(json, fullPath);
        }
        else if (fullPath.Contains($"{Path.DirectorySeparatorChar}assertions{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            _ = ContentStudioJson.Deserialize<ContentAssertionDefinition>(json, fullPath);
        }
        else
        {
            throw new ContentStudioException("Only project, recipe, workbench, entry, and assertion JSON manifests are editable here.");
        }
        var normalized = json.TrimEnd() + Environment.NewLine;
        AtomicFile.WriteAllText(fullPath, normalized);
        return Fingerprint(normalized);
    }

    public string Version(string path) => ReadSnapshot(path).Version;

    private static string Fingerprint(string contents) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));

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

public sealed record ManifestSnapshot(string Path, string Contents, string Version);
