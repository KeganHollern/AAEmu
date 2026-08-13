using System.Text.Json;
using AAEmu.ContentStudio.Core;
using AAEmu.ContentStudio.Core.Models;
using AAEmu.ContentStudio.Core.Services;

return await ContentStudioCli.RunAsync(args);

internal static class ContentStudioCli
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return Task.FromResult(0);
            }

            var command = args[0].ToLowerInvariant();
            var options = CommandOptions.Parse(args.Skip(1).ToArray());
            return Task.FromResult(command switch
            {
                "doctor" => Doctor(options),
                "verify" => Verify(options),
                "validate" => Validate(options),
                "schema" => Schema(options),
                "search" => Search(options),
                "items" => Items(options),
                "recipe" => Recipe(options),
                "workbench" => Workbench(options),
                "scaffold-recipe" => ScaffoldRecipe(options),
                "scaffold-workbench" => ScaffoldWorkbench(options),
                "build" => Build(options),
                "diff" => Diff(options),
                "deploy" => Deploy(options),
                "rollback" => Rollback(options),
                _ => throw new ContentStudioException($"Unknown command '{command}'. Run 'aaemu-content help'.")
            });
        }
        catch (Exception exception) when (exception is ContentStudioException or IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return Task.FromResult(1);
        }
    }

    private static int Doctor(CommandOptions options)
    {
        var configuration = LoadConfiguration(options);
        return PrintValidation(new DoctorService().Diagnose(configuration));
    }

    private static int Verify(CommandOptions options)
    {
        var descriptorPath = options.Require("descriptor");
        var descriptor = new ProjectRepository().LoadBaseline(descriptorPath);
        return PrintValidation(new BaselineVerifier().Verify(options.Require("compact"), descriptor));
    }

    private static int Validate(CommandOptions options)
    {
        var configuration = LoadConfiguration(options);
        var project = new ProjectRepository().LoadProject(configuration.ProjectPath);
        return PrintValidation(new ContentValidator().ValidateProject(project, configuration.BaselinePath));
    }

    private static int Schema(CommandOptions options)
    {
        PrintJson(new CompactCatalogService().ListSchema(options.Require("compact"), options.Get("filter")));
        return 0;
    }

    private static int Items(CommandOptions options)
    {
        PrintJson(new CompactCatalogService().SearchItems(options.Require("compact"), options.Require("query"), options.Get("language") ?? "en_us", options.GetInt("limit", 50)));
        return 0;
    }

    private static int Search(CommandOptions options)
    {
        PrintJson(new CatalogSearchService().SearchEverything(
            options.Require("compact"),
            options.Require("query"),
            options.Get("language") ?? "en_us",
            options.GetInt("limit", 80)));
        return 0;
    }

    private static int Recipe(CommandOptions options)
    {
        var id = options.RequireUInt("id");
        PrintJson(new CompactCatalogService().GetRecipe(options.Require("compact"), id, options.Get("language") ?? "en_us")
            ?? throw new ContentStudioException($"Recipe {id} was not found."));
        return 0;
    }

    private static int Workbench(CommandOptions options)
    {
        var id = options.RequireUInt("id");
        PrintJson(new CompactCatalogService().GetWorkbench(options.Require("compact"), id, options.Get("language") ?? "en_us")
            ?? throw new ContentStudioException($"Workbench {id} was not found."));
        return 0;
    }

    private static int ScaffoldRecipe(CommandOptions options)
    {
        var configuration = LoadConfiguration(options);
        var result = new ScaffoldService().CreateRecipe(new RecipeScaffoldRequest
        {
            ProjectPath = configuration.ProjectPath,
            BaselinePath = configuration.BaselinePath,
            Key = options.Require("key"),
            SourceRecipeId = options.RequireUInt("source"),
            Name = options.Get("name") ?? string.Empty,
            CraftPackIds = options.GetUIntList("packs"),
            CloneSkill = options.HasFlag("clone-skill"),
            DryRun = options.HasFlag("dry-run")
        });
        PrintJson(result);
        return 0;
    }

    private static int ScaffoldWorkbench(CommandOptions options)
    {
        var configuration = LoadConfiguration(options);
        var result = new ScaffoldService().CreateWorkbench(new WorkbenchScaffoldRequest
        {
            ProjectPath = configuration.ProjectPath,
            BaselinePath = configuration.BaselinePath,
            Key = options.Require("key"),
            SourceDoodadId = options.RequireUInt("source"),
            Name = options.Get("name") ?? string.Empty,
            RecipeIds = options.GetUIntList("recipes") ?? [],
            DryRun = options.HasFlag("dry-run")
        });
        PrintJson(result);
        return 0;
    }

    private static int Build(CommandOptions options)
    {
        var configuration = LoadConfiguration(options);
        var result = new BuildService().Build(new ContentBuildRequest
        {
            BaselinePath = configuration.BaselinePath,
            BaselineDescriptorPath = configuration.BaselineDescriptorPath,
            ProjectPath = configuration.ProjectPath,
            OutputDirectory = configuration.OutputDirectory,
            KeepStagingOnFailure = options.HasFlag("keep-staging")
        });
        PrintJson(result);
        return 0;
    }

    private static int Diff(CommandOptions options)
    {
        PrintJson(new DatabaseDiffService().Compare(options.Require("baseline"), options.Require("artifact")));
        return 0;
    }

    private static int Deploy(CommandOptions options)
    {
        var configuration = LoadConfiguration(options);
        var targetName = options.Require("target");
        if (!configuration.Targets.TryGetValue(targetName, out var target))
        {
            throw new ContentStudioException($"Deployment target '{targetName}' is not configured.");
        }
        if (options.HasFlag("dry-run"))
        {
            var artifact = Path.GetFullPath(options.Require("artifact"));
            PrintJson(new { dryRun = true, target = targetName, targetPath = target.Path, artifactPath = artifact, artifactSha256 = FileHashService.CalculateSha256(artifact), willBackup = File.Exists(target.Path) });
            return 0;
        }
        var manifest = new DeploymentService().Deploy(options.Require("artifact"), targetName, target, configuration.OutputDirectory);
        PrintJson(manifest);
        return 0;
    }

    private static int Rollback(CommandOptions options)
    {
        var configuration = LoadConfiguration(options);
        var targetName = options.Require("target");
        if (!configuration.Targets.TryGetValue(targetName, out var target))
        {
            throw new ContentStudioException($"Deployment target '{targetName}' is not configured.");
        }
        if (options.HasFlag("dry-run"))
        {
            PrintJson(new { dryRun = true, target = targetName, targetPath = target.Path, backupPath = Path.GetFullPath(options.Require("backup")) });
            return 0;
        }
        new DeploymentService().Rollback(target.Path, options.Require("backup"));
        Console.WriteLine($"Restored {targetName} from {Path.GetFullPath(options.Require("backup"))}");
        return 0;
    }

    private static StudioConfiguration LoadConfiguration(CommandOptions options)
        => new ProjectRepository().LoadConfiguration(options.Get("config") ?? "Content/content-studio.json");

    private static int PrintValidation(ValidationReport report)
    {
        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}");
        }
        Console.WriteLine(report.IsValid ? $"PASS ({report.WarningCount} warning(s))" : $"FAIL ({report.ErrorCount} error(s), {report.WarningCount} warning(s))");
        return report.IsValid ? 0 : 1;
    }

    private static void PrintJson<T>(T value) => Console.WriteLine(ContentStudioJson.Serialize(value));

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AAEmu Content Studio

            Safety and build:
              doctor --config <file>
              verify --compact <db> --descriptor <json>
              validate --config <file>
              build --config <file> [--keep-staging]
              diff --baseline <db> --artifact <db>
              deploy --config <file> --artifact <db> --target <name> [--dry-run]
              rollback --config <file> --target <name> --backup <db> [--dry-run]

            Inspect:
              search --compact <db> --query <name-or-id> [--language en_us] [--limit 80]
              schema --compact <db> [--filter <text>]
              items --compact <db> --query <text-or-id> [--language en_us] [--limit 50]
              recipe --compact <db> --id <id>
              workbench --compact <db> --id <id>

            Create editable manifests:
              scaffold-recipe --config <file> --key <key> --source <recipe-id> [--name <name>] [--packs 1,2] [--clone-skill] [--dry-run]
              scaffold-workbench --config <file> --key <key> --source <doodad-id> [--name <name>] [--recipes 1,2] [--dry-run]
            """);
    }
}

internal sealed class CommandOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CommandOptions Parse(string[] args)
    {
        var result = new CommandOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ContentStudioException($"Unexpected argument '{token}'. Options must start with --.");
            }
            var key = token[2..];
            string? value = null;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }
            result._values[key] = value;
        }
        return result;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);
    public bool HasFlag(string key) => _values.ContainsKey(key) && _values[key] is null;
    public string Require(string key) => Get(key) ?? throw new ContentStudioException($"Missing required option --{key}.");
    public uint RequireUInt(string key) => uint.TryParse(Require(key), out var value) ? value : throw new FormatException($"--{key} must be an unsigned integer.");
    public int GetInt(string key, int defaultValue) => Get(key) is { } text ? int.Parse(text) : defaultValue;
    public uint[]? GetUIntList(string key) => Get(key) is { } text
        ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(uint.Parse).ToArray()
        : null;
}
