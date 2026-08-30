using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class BuildService
{
    private readonly ProjectRepository _repository = new();
    private readonly BaselineVerifier _baselineVerifier = new();
    private readonly ContentValidator _validator = new();
    private readonly Action? _beforePromotion;

    public BuildService()
    {
    }

    internal BuildService(Action beforePromotion)
    {
        _beforePromotion = beforePromotion;
    }

    public ContentBuildResult Build(ContentBuildRequest request)
    {
        var baselinePath = Path.GetFullPath(request.BaselinePath);
        var descriptor = _repository.LoadBaseline(request.BaselineDescriptorPath);
        var discoveredProject = _repository.LoadProject(request.ProjectPath);
        var sourceSnapshots = CaptureSources(discoveredProject.SourceFiles);
        var sourceContents = sourceSnapshots.ToDictionary(snapshot => snapshot.Path, snapshot => snapshot.Contents, StringComparer.OrdinalIgnoreCase);
        var project = _repository.LoadProject(request.ProjectPath, sourceContents);
        EnsureSourceSet(project.SourceFiles, sourceSnapshots);
        if (!project.Definition.TargetBaseline.Equals(descriptor.Key, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStudioException($"Project targets '{project.Definition.TargetBaseline}', not baseline '{descriptor.Key}'.");
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var stagingDirectory = Path.Combine(outputDirectory, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, "compact.sqlite3");
        var artifactPath = Path.Combine(outputDirectory, $"compact.{project.Definition.Key}.sqlite3");
        var manifestPath = Path.Combine(outputDirectory, "content-build-manifest.json");
        var reportPath = Path.Combine(outputDirectory, "content-build-report.md");
        var auditSqlPath = Path.Combine(outputDirectory, "content-build-audit.sql");
        var towerDefenseBundlePath = Path.Combine(outputDirectory, $"tower-defense.{project.Definition.Key}.json");

        try
        {
            File.Copy(baselinePath, stagingPath, true);
            ThrowIfInvalid(_baselineVerifier.Verify(stagingPath, descriptor), "Baseline verification failed");
            ThrowIfInvalid(_validator.ValidateProject(project, stagingPath), "Project validation failed");
            var changes = Compile(stagingPath, project, sourceContents);
            var towerDefenseBundle = CompileTowerDefenseBundle(project, sourceContents);
            if (towerDefenseBundle != null)
            {
                changes.AddRange(towerDefenseBundle.EventKeys.Select(key =>
                    new ContentChange("tower-defense", key, 0, "bundle", "Validated runtime event manifest")));
            }
            var validation = _validator.ValidateBuiltDatabase(stagingPath, project);
            ThrowIfInvalid(validation, "Compiled database validation failed");
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
                SourceHashes = sourceSnapshots.ToDictionary(
                    snapshot => Path.GetRelativePath(project.ProjectDirectory, snapshot.Path).Replace('\\', '/'),
                    snapshot => snapshot.Hash,
                    StringComparer.OrdinalIgnoreCase),
                ArtifactPath = artifactPath,
                ArtifactSha256 = FileHashService.CalculateSha256(stagingPath),
                AuditSqlPath = auditSqlPath,
                ArtifactLength = new FileInfo(stagingPath).Length,
                RecipeCount = project.Recipes.Count,
                WorkbenchCount = project.Workbenches.Count,
                AssertionCount = project.Assertions.Count,
                TowerDefenseEventCount = towerDefenseBundle?.EventKeys.Count ?? 0,
                TowerDefenseBundlePath = towerDefenseBundle == null ? null : towerDefenseBundlePath,
                TowerDefenseBundleSha256 = towerDefenseBundle?.Hash,
                Validation = validation,
                Changes = changes
            };
            var manifestContents = ContentStudioJson.Serialize(manifest) + Environment.NewLine;
            var reportContents = CreateReport(manifest);
            var auditSqlContents = CreateAuditSql(project);
            _beforePromotion?.Invoke();
            var outputs = new List<BuildOutput>
            {
                new BuildOutput(artifactPath, stagingPath, null, manifest.ArtifactSha256),
                new BuildOutput(manifestPath, null, manifestContents, HashText(manifestContents)),
                new BuildOutput(reportPath, null, reportContents, HashText(reportContents)),
                new BuildOutput(auditSqlPath, null, auditSqlContents, HashText(auditSqlContents))
            };
            if (towerDefenseBundle != null)
                outputs.Add(new BuildOutput(towerDefenseBundlePath, null,
                    towerDefenseBundle.Contents, towerDefenseBundle.Hash));
            PublishBuildOutputs(outputs, () => EnsureSourcesUnchanged(request.ProjectPath, sourceSnapshots));
            return new ContentBuildResult
            {
                ArtifactPath = artifactPath,
                ManifestPath = manifestPath,
                ReportPath = reportPath,
                AuditSqlPath = auditSqlPath,
                TowerDefenseBundlePath = towerDefenseBundle == null ? null : towerDefenseBundlePath,
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

    private static TowerDefenseBundle? CompileTowerDefenseBundle(
        LoadedContentProject project,
        IReadOnlyDictionary<string, string> sourceContents)
    {
        if (project.TowerDefenseFiles.Count == 0)
            return null;

        var events = new JsonArray();
        var eventKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in project.TowerDefenseFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            JsonObject root;
            try
            {
                root = JsonNode.Parse(sourceContents[path]) as JsonObject
                    ?? throw new ContentStudioException($"Tower-defense source must be a JSON object: {path}");
            }
            catch (Exception exception) when (exception is not ContentStudioException)
            {
                throw new ContentStudioException($"Unable to parse tower-defense source {path}: {exception.Message}", exception);
            }

            if (root["schemaVersion"]?.GetValue<int>() != 1 || root["events"] is not JsonArray sourceEvents)
                throw new ContentStudioException($"Tower-defense source must use schemaVersion 1 and contain an events array: {path}");
            foreach (var node in sourceEvents)
            {
                if (node is not JsonObject eventNode ||
                    string.IsNullOrWhiteSpace(eventNode["key"]?.GetValue<string>()) ||
                    eventNode["towerDefId"]?.GetValue<uint>() is not > 0 ||
                    string.IsNullOrWhiteSpace(eventNode["worldTemplate"]?.GetValue<string>()) ||
                    eventNode["sites"] is not JsonArray { Count: > 0 })
                {
                    throw new ContentStudioException($"Tower-defense source has an event with invalid required fields: {path}");
                }
                var key = eventNode["key"]!.GetValue<string>();
                if (!eventKeys.Add(key))
                    throw new ContentStudioException($"Tower-defense event key '{key}' is duplicated across project sources.");
                events.Add(eventNode.DeepClone());
            }
        }

        var contents = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["events"] = events
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        return new TowerDefenseBundle(contents, HashText(contents), eventKeys.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<ContentChange> Compile(
        string compactPath,
        LoadedContentProject project,
        IReadOnlyDictionary<string, string> sourceContents)
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
            command.CommandText = sourceContents[rawSqlPath];
            command.ExecuteNonQuery();
            changes.Add(new ContentChange("raw-sql", Path.GetFileName(rawSqlPath), 0, "execute", Path.GetFileName(rawSqlPath)));
        }
        transaction.Commit();
        return changes;
    }

    private static List<SourceSnapshot> CaptureSources(IEnumerable<string> paths)
    {
        var result = new List<SourceSnapshot>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(path);
            var bytes = File.ReadAllBytes(fullPath);
            result.Add(new SourceSnapshot(fullPath, bytes, DecodeText(bytes), Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return result;
    }

    private void EnsureSourcesUnchanged(string projectPath, IReadOnlyList<SourceSnapshot> snapshots)
    {
        LoadedContentProject current;
        try
        {
            current = _repository.LoadProject(projectPath);
        }
        catch (Exception exception)
        {
            throw new ContentStudioException("Project sources changed while the build was running. Build again from the latest saved changes.", exception);
        }
        EnsureSourceSet(current.SourceFiles, snapshots);
        foreach (var snapshot in snapshots)
        {
            if (!File.Exists(snapshot.Path) || !File.ReadAllBytes(snapshot.Path).AsSpan().SequenceEqual(snapshot.Bytes))
            {
                throw new ContentStudioException($"Project source changed while the build was running: {snapshot.Path}. Build again from the latest saved changes.");
            }
        }
    }

    private static void EnsureSourceSet(IEnumerable<string> paths, IReadOnlyList<SourceSnapshot> snapshots)
    {
        var current = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var captured = snapshots.Select(snapshot => snapshot.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!current.SetEquals(captured))
        {
            throw new ContentStudioException("Project source files changed while the build snapshot was being prepared. Build again from the latest saved changes.");
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void PublishBuildOutputs(IReadOnlyList<BuildOutput> outputs, Action verifySources)
    {
        lock (AtomicFile.SyncRoot)
        {
            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var output in outputs)
                {
                    var directory = Path.GetDirectoryName(output.Path)
                        ?? throw new ContentStudioException($"Unable to determine the build output directory for {output.Path}.");
                    Directory.CreateDirectory(directory);
                    var stagingPath = Path.Combine(directory, $".{Path.GetFileName(output.Path)}.{Guid.NewGuid():N}.tmp");
                    if (output.SourcePath is not null)
                    {
                        File.Copy(output.SourcePath, stagingPath, true);
                    }
                    else
                    {
                        File.WriteAllText(stagingPath, output.Contents!, new UTF8Encoding(false));
                    }
                    staged[output.Path] = stagingPath;
                    EnsureOutputHash(stagingPath, output);
                }

                verifySources();
                foreach (var output in outputs.Where(output => File.Exists(output.Path)))
                {
                    var directory = Path.GetDirectoryName(output.Path)!;
                    var backupPath = Path.Combine(directory, $".{Path.GetFileName(output.Path)}.{Guid.NewGuid():N}.bak");
                    File.Copy(output.Path, backupPath, true);
                    backups[output.Path] = backupPath;
                }
                verifySources();
                foreach (var output in outputs) EnsureOutputHash(staged[output.Path], output);

                var applied = new List<BuildOutput>();
                try
                {
                    foreach (var output in outputs)
                    {
                        EnsureOutputHash(staged[output.Path], output);
                        File.Move(staged[output.Path], output.Path, true);
                        applied.Add(output);
                        EnsureOutputHash(output.Path, output);
                    }
                    verifySources();
                }
                catch (Exception exception)
                {
                    Exception? rollbackFailure = null;
                    foreach (var output in applied.AsEnumerable().Reverse())
                    {
                        try
                        {
                            EnsureOutputHash(output.Path, output);
                            if (backups.TryGetValue(output.Path, out var backupPath))
                            {
                                File.Move(backupPath, output.Path, true);
                            }
                            else
                            {
                                File.Delete(output.Path);
                            }
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailure ??= rollbackException;
                        }
                    }
                    throw rollbackFailure is null
                        ? new ContentStudioException("Build publication failed. Previous build outputs were restored.", exception)
                        : new ContentStudioException("Build publication failed and at least one previous output could not be restored. Recover the build directory before deploying.", new AggregateException(exception, rollbackFailure));
                }
            }
            finally
            {
                foreach (var path in staged.Values.Concat(backups.Values))
                {
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }
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
        builder.AppendLine($"- Artifact assertions: {manifest.AssertionCount}");
        builder.AppendLine($"- Tower-defense events: {manifest.TowerDefenseEventCount}");
        if (manifest.TowerDefenseBundlePath != null)
            builder.AppendLine($"- Tower-defense runtime bundle: `{manifest.TowerDefenseBundlePath}` / `{manifest.TowerDefenseBundleSha256}`");
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
        foreach (var group in project.Records.GroupBy(record => record.Table, StringComparer.OrdinalIgnoreCase))
        {
            var ids = IdList(group.Select(record => record.Id));
            builder.AppendLine($"SELECT * FROM {BaselineVerifier.QuoteIdentifier(group.Key)} WHERE id IN ({ids}) ORDER BY id;");
        }
        if (project.Assertions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("-- Project assertions (each query should return its documented expected value)");
            foreach (var assertion in project.Assertions)
            {
                builder.AppendLine($"-- {SqlCommentText(assertion.Key)}: expected {SqlCommentText(assertion.Expected)} — {SqlCommentText(assertion.Description)}");
                builder.AppendLine(assertion.Query.TrimEnd().TrimEnd(';') + ";");
            }
        }
        return builder.ToString();
    }

    private static string SqlCommentText(string value) => new(value
        .Select(character => char.IsControl(character) || character is '\u0085' or '\u2028' or '\u2029' ? ' ' : character)
        .ToArray());

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void EnsureOutputHash(string path, BuildOutput output)
    {
        if (!File.Exists(path) || !FileHashService.CalculateSha256(path).Equals(output.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStudioException($"Build output changed while it was being published: {output.Path}.");
        }
    }

    private sealed record SourceSnapshot(string Path, byte[] Bytes, string Contents, string Hash);
    private sealed record BuildOutput(string Path, string? SourcePath, string? Contents, string ExpectedSha256);
    private sealed record TowerDefenseBundle(string Contents, string Hash, IReadOnlyList<string> EventKeys);
}
