using System.Reflection;
using System.Text;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class BuildService
{
    private readonly ProjectRepository _repository = new();
    private readonly BaselineVerifier _baselineVerifier = new();
    private readonly ContentValidator _validator = new();

    public ContentBuildResult Build(ContentBuildRequest request)
    {
        var baselinePath = Path.GetFullPath(request.BaselinePath);
        var descriptor = _repository.LoadBaseline(request.BaselineDescriptorPath);
        var project = _repository.LoadProject(request.ProjectPath);
        if (!project.Definition.TargetBaseline.Equals(descriptor.Key, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStudioException($"Project targets '{project.Definition.TargetBaseline}', not baseline '{descriptor.Key}'.");
        }

        ThrowIfInvalid(_baselineVerifier.Verify(baselinePath, descriptor), "Baseline verification failed");
        ThrowIfInvalid(_validator.ValidateProject(project, baselinePath), "Project validation failed");

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var stagingDirectory = Path.Combine(outputDirectory, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, "compact.sqlite3");
        var artifactPath = Path.Combine(outputDirectory, $"compact.{project.Definition.Key}.sqlite3");
        var manifestPath = Path.Combine(outputDirectory, "content-build-manifest.json");
        var reportPath = Path.Combine(outputDirectory, "content-build-report.md");
        var auditSqlPath = Path.Combine(outputDirectory, "content-build-audit.sql");

        try
        {
            File.Copy(baselinePath, stagingPath, true);
            var changes = Compile(stagingPath, project);
            var validation = _validator.ValidateBuiltDatabase(stagingPath, project);
            ThrowIfInvalid(validation, "Compiled database validation failed");
            AtomicFile.ReplaceFrom(stagingPath, artifactPath);
            validation.Issues = validation.Issues
                .Select(issue => issue.Source?.Equals(stagingPath, StringComparison.OrdinalIgnoreCase) == true ? issue with { Source = artifactPath } : issue)
                .ToList();

            var manifest = new ContentBuildManifest
            {
                ToolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "development",
                BuiltAtUtc = DateTimeOffset.UtcNow,
                BaselineKey = descriptor.Key,
                BaselineSha256 = descriptor.Sha256,
                ProjectKey = project.Definition.Key,
                SourceHashes = project.SourceFiles.ToDictionary(
                    file => Path.GetRelativePath(project.ProjectDirectory, file).Replace('\\', '/'),
                    FileHashService.CalculateSha256,
                    StringComparer.OrdinalIgnoreCase),
                ArtifactPath = artifactPath,
                ArtifactSha256 = FileHashService.CalculateSha256(artifactPath),
                AuditSqlPath = auditSqlPath,
                ArtifactLength = new FileInfo(artifactPath).Length,
                RecipeCount = project.Recipes.Count,
                WorkbenchCount = project.Workbenches.Count,
                Validation = validation,
                Changes = changes
            };
            AtomicFile.WriteAllText(manifestPath, ContentStudioJson.Serialize(manifest) + Environment.NewLine);
            AtomicFile.WriteAllText(reportPath, CreateReport(manifest));
            AtomicFile.WriteAllText(auditSqlPath, CreateAuditSql(project));
            return new ContentBuildResult
            {
                ArtifactPath = artifactPath,
                ManifestPath = manifestPath,
                ReportPath = reportPath,
                AuditSqlPath = auditSqlPath,
                Manifest = manifest
            };
        }
        finally
        {
            if (Directory.Exists(stagingDirectory) && (!request.KeepStagingOnFailure || File.Exists(artifactPath)))
            {
                Directory.Delete(stagingDirectory, true);
            }
        }
    }

    private static List<ContentChange> Compile(string compactPath, LoadedContentProject project)
    {
        using var connection = CompactConnectionFactory.OpenReadWrite(compactPath);
        using var transaction = connection.BeginTransaction();
        var changes = new List<ContentChange>();
        var workbenchCompiler = new WorkbenchCompiler();
        var recipeCompiler = new RecipeCompiler();
        var recordCompiler = new RecordCompiler();

        foreach (var record in project.Records.OrderBy(record => record.Table).ThenBy(record => record.Id))
        {
            changes.AddRange(recordCompiler.Compile(connection, transaction, record));
        }

        foreach (var workbench in project.Workbenches.OrderBy(workbench => workbench.Id))
        {
            changes.AddRange(workbenchCompiler.Compile(connection, transaction, workbench));
        }
        foreach (var recipe in project.Recipes.OrderBy(recipe => recipe.Id))
        {
            changes.AddRange(recipeCompiler.Compile(connection, transaction, recipe));
        }
        foreach (var rawSqlPath in project.RawSqlFiles)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = File.ReadAllText(rawSqlPath);
            command.ExecuteNonQuery();
            changes.Add(new ContentChange("raw-sql", Path.GetFileName(rawSqlPath), 0, "execute", Path.GetFileName(rawSqlPath)));
        }
        transaction.Commit();
        return changes;
    }

    private static void ThrowIfInvalid(ValidationReport report, string heading)
    {
        if (report.IsValid)
        {
            return;
        }
        throw new ContentStudioException($"{heading}:{Environment.NewLine}{string.Join(Environment.NewLine, report.Issues.Where(issue => issue.Severity == ValidationSeverity.Error).Select(issue => $"- [{issue.Code}] {issue.Message}"))}");
    }

    private static string CreateReport(ContentBuildManifest manifest)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AAEmu Content Build Report").AppendLine();
        builder.AppendLine($"- Built: {manifest.BuiltAtUtc:O}");
        builder.AppendLine($"- Baseline: `{manifest.BaselineKey}` / `{manifest.BaselineSha256}`");
        builder.AppendLine($"- Project: `{manifest.ProjectKey}`");
        builder.AppendLine($"- Artifact: `{manifest.ArtifactPath}`");
        builder.AppendLine($"- SHA-256: `{manifest.ArtifactSha256}`");
        builder.AppendLine($"- Recipes: {manifest.RecipeCount}");
        builder.AppendLine($"- Workbenches: {manifest.WorkbenchCount}").AppendLine();
        builder.AppendLine($"- Other changed or copied entries: {manifest.Changes.Count(change => change.EntityType == "record")}").AppendLine();
        builder.AppendLine("## Changes").AppendLine();
        foreach (var change in manifest.Changes)
        {
            builder.AppendLine($"- **{change.EntityType}** `{change.Key}` ({change.Id}): {change.Summary}");
        }
        builder.AppendLine().AppendLine("## Validation").AppendLine();
        foreach (var issue in manifest.Validation.Issues)
        {
            builder.AppendLine($"- {issue.Severity}: `{issue.Code}` — {issue.Message}");
        }
        return builder.ToString();
    }

    private static string CreateAuditSql(LoadedContentProject project)
    {
        static string IdList(IEnumerable<uint> ids) => string.Join(", ", ids.Distinct().Order());
        var builder = new StringBuilder();
        builder.AppendLine("-- AAEmu Content Studio generated verification queries");
        builder.AppendLine("-- Run against the built artifact; this file does not mutate the database.").AppendLine();
        if (project.Recipes.Count > 0)
        {
            var ids = IdList(project.Recipes.Select(recipe => recipe.Id));
            builder.AppendLine($"SELECT * FROM crafts WHERE id IN ({ids}) ORDER BY id;");
            builder.AppendLine($"SELECT * FROM craft_materials WHERE craft_id IN ({ids}) ORDER BY craft_id, id;");
            builder.AppendLine($"SELECT * FROM craft_products WHERE craft_id IN ({ids}) ORDER BY craft_id, id;");
            builder.AppendLine($"SELECT * FROM craft_pack_crafts WHERE craft_id IN ({ids}) ORDER BY craft_pack_id, craft_id;");
        }
        if (project.Workbenches.Count > 0)
        {
            var doodadIds = IdList(project.Workbenches.Select(workbench => workbench.Id));
            var packIds = IdList(project.Workbenches.Select(workbench => workbench.CraftPack.Id));
            builder.AppendLine($"SELECT * FROM doodad_almighties WHERE id IN ({doodadIds}) ORDER BY id;");
            builder.AppendLine($"SELECT * FROM doodad_func_groups WHERE doodad_almighty_id IN ({doodadIds}) ORDER BY doodad_almighty_id, id;");
            builder.AppendLine($"SELECT * FROM craft_pack_crafts WHERE craft_pack_id IN ({packIds}) ORDER BY craft_pack_id, craft_id;");
        }
        return builder.ToString();
    }
}
