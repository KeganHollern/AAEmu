using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class ProjectRepository
{
    public BaselineDescriptor LoadBaseline(string descriptorPath)
    {
        var fullPath = RequireFile(descriptorPath, "Baseline descriptor");
        return ContentStudioJson.Deserialize<BaselineDescriptor>(File.ReadAllText(fullPath), fullPath);
    }

    public StudioConfiguration LoadConfiguration(string configurationPath)
    {
        var fullPath = RequireFile(configurationPath, "Content Studio configuration");
        var configuration = ContentStudioJson.Deserialize<StudioConfiguration>(File.ReadAllText(fullPath), fullPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ContentStudioException($"Unable to determine the configuration directory for {fullPath}.");
        configuration.BaselinePath = ResolvePath(directory, configuration.BaselinePath);
        configuration.BaselineDescriptorPath = ResolvePath(directory, configuration.BaselineDescriptorPath);
        configuration.ProjectPath = ResolvePath(directory, configuration.ProjectPath);
        configuration.OutputDirectory = ResolvePath(directory, configuration.OutputDirectory);
        foreach (var target in configuration.Targets.Values)
        {
            target.Path = ResolvePath(directory, target.Path);
            target.BackupDirectory = ResolvePath(directory, target.BackupDirectory);
        }
        return configuration;
    }

    private static string ResolvePath(string directory, string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory, path));
    }

    public LoadedContentProject LoadProject(string projectPath)
    {
        var fullProjectPath = RequireFile(projectPath, "Content project");
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)
            ?? throw new ContentStudioException($"Unable to determine the project directory for {fullProjectPath}.");
        var definition = ContentStudioJson.Deserialize<ContentProjectDefinition>(File.ReadAllText(fullProjectPath), fullProjectPath);
        var registryPath = RequireFile(Path.Combine(projectDirectory, definition.IdRegistry), "ID registry");
        var registry = ContentStudioJson.Deserialize<IdRegistry>(File.ReadAllText(registryPath), registryPath);

        var recipeFiles = ExpandPatterns(projectDirectory, definition.Recipes);
        var workbenchFiles = ExpandPatterns(projectDirectory, definition.Workbenches);
        var recordFiles = ExpandPatterns(projectDirectory, definition.Records);
        var assertionFiles = ExpandPatterns(projectDirectory, definition.Assertions);
        var rawSqlFiles = ExpandPatterns(projectDirectory, definition.RawSql);
        var recipes = recipeFiles.Select(path => ContentStudioJson.Deserialize<RecipeDefinition>(File.ReadAllText(path), path)).ToList();
        var workbenches = workbenchFiles.Select(path => ContentStudioJson.Deserialize<WorkbenchDefinition>(File.ReadAllText(path), path)).ToList();
        var records = recordFiles.Select(path => ContentStudioJson.Deserialize<RecordDefinition>(File.ReadAllText(path), path)).ToList();
        var assertions = assertionFiles.Select(path => ContentStudioJson.Deserialize<ContentAssertionDefinition>(File.ReadAllText(path), path)).ToList();

        var sourceFiles = new[] { fullProjectPath, registryPath }
            .Concat(recipeFiles)
            .Concat(workbenchFiles)
            .Concat(recordFiles)
            .Concat(assertionFiles)
            .Concat(rawSqlFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LoadedContentProject
        {
            ProjectDirectory = projectDirectory,
            Definition = definition,
            Registry = registry,
            Recipes = recipes,
            Workbenches = workbenches,
            Records = records,
            Assertions = assertions,
            RawSqlFiles = rawSqlFiles,
            SourceFiles = sourceFiles
        };
    }

    public void SaveRegistry(string projectPath, IdRegistry registry)
    {
        var fullProjectPath = RequireFile(projectPath, "Content project");
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)
            ?? throw new ContentStudioException($"Unable to determine the project directory for {fullProjectPath}.");
        var definition = ContentStudioJson.Deserialize<ContentProjectDefinition>(File.ReadAllText(fullProjectPath), fullProjectPath);
        var registryPath = Path.GetFullPath(Path.Combine(projectDirectory, definition.IdRegistry));
        AtomicFile.WriteAllText(registryPath, ContentStudioJson.Serialize(registry) + Environment.NewLine);
    }

    private static string RequireFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ContentStudioException($"{description} does not exist: {fullPath}");
        }

        return fullPath;
    }

    private static List<string> ExpandPatterns(string projectDirectory, IEnumerable<string> patterns)
    {
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            var combined = Path.GetFullPath(Path.Combine(projectDirectory, pattern));
            var directory = Path.GetDirectoryName(combined);
            var searchPattern = Path.GetFileName(combined);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                files.Add(Path.GetFullPath(file));
            }
        }

        return files.ToList();
    }
}
