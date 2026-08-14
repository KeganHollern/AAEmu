using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class DeploymentService
{
    private readonly Action<int, string>? _afterTargetReplace;

    public DeploymentService()
    {
    }

    internal DeploymentService(Action<int, string> afterTargetReplace)
    {
        _afterTargetReplace = afterTargetReplace;
    }

    public string ValidateReviewedArtifact(string artifactPath, string expectedArtifactSha256, DeploymentTarget target)
    {
        var artifact = Path.GetFullPath(artifactPath);
        if (!File.Exists(artifact)) throw new ContentStudioException($"Database does not exist: {artifact}");
        if (string.IsNullOrWhiteSpace(expectedArtifactSha256))
            throw new ContentStudioException("Choose a reviewed build before validating it for publication.");
        var targetPath = Path.GetFullPath(target.Path);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new ContentStudioException($"Unable to determine deployment directory for {targetPath}.");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(targetDirectory, $".compact.review.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(artifact, temporaryPath, true);
            var hash = FileHashService.CalculateSha256(temporaryPath);
            if (!hash.Equals(expectedArtifactSha256, StringComparison.OrdinalIgnoreCase))
                throw new ContentStudioException("The build artifact changed after it was reviewed. Prepare and review the changes again before publishing.");
            RequireValidDatabase(temporaryPath);
            if (File.Exists(targetPath))
            {
                RequireValidDatabase(targetPath);
                RequireCompatibleSchema(temporaryPath, targetPath);
            }
            return hash;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public DeploymentManifest Deploy(string artifactPath, string expectedArtifactSha256, string targetName, DeploymentTarget target, string manifestDirectory)
    {
        var artifact = Path.GetFullPath(artifactPath);
        if (!File.Exists(artifact)) throw new ContentStudioException($"Database does not exist: {artifact}");
        if (string.IsNullOrWhiteSpace(expectedArtifactSha256))
            throw new ContentStudioException("Choose a reviewed build before publishing it.");
        var targetPath = Path.GetFullPath(target.Path);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new ContentStudioException($"Unable to determine deployment directory for {targetPath}.");
        Directory.CreateDirectory(targetDirectory);

        var temporaryPath = Path.Combine(targetDirectory, $".compact.deploy.{Guid.NewGuid():N}.tmp");
        string? previousHash = null;
        string? backupPath = null;
        try
        {
            File.Copy(artifact, temporaryPath, true);
            var artifactHash = FileHashService.CalculateSha256(temporaryPath);
            if (!artifactHash.Equals(expectedArtifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ContentStudioException("The build artifact changed after it was reviewed. Prepare and review the changes again before publishing.");
            }
            RequireValidDatabase(temporaryPath);

            if (File.Exists(targetPath))
            {
                RequireValidDatabase(targetPath);
                RequireCompatibleSchema(temporaryPath, targetPath);
                previousHash = FileHashService.CalculateSha256(targetPath);
                var backupDirectory = Path.GetFullPath(target.BackupDirectory);
                Directory.CreateDirectory(backupDirectory);
                backupPath = Path.Combine(backupDirectory, $"compact.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.{previousHash[..12]}.sqlite3");
                File.Copy(targetPath, backupPath, false);
            }
            Directory.CreateDirectory(manifestDirectory);
            var deployedAt = DateTimeOffset.UtcNow;
            var manifestPath = Path.Combine(manifestDirectory, $"deployment-{targetName}-{deployedAt:yyyyMMdd-HHmmss}.json");
            if (!FileHashService.CalculateSha256(temporaryPath).Equals(expectedArtifactSha256, StringComparison.OrdinalIgnoreCase))
                throw new ContentStudioException("The staged build artifact changed before publication.");
            File.Move(temporaryPath, targetPath, true);
            try
            {
                _afterTargetReplace?.Invoke(0, targetPath);
                RequireValidDatabase(targetPath);
                if (!FileHashService.CalculateSha256(targetPath).Equals(expectedArtifactSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ContentStudioException("The published database does not match the reviewed build hash.");
                var manifest = new DeploymentManifest
                {
                    DeployedAtUtc = deployedAt,
                    TargetName = targetName,
                    TargetPath = targetPath,
                    ArtifactPath = artifact,
                    ArtifactSha256 = artifactHash,
                    PreviousSha256 = previousHash,
                    BackupPath = backupPath
                };
                _afterTargetReplace?.Invoke(1, manifestPath);
                AtomicFile.WriteAllText(manifestPath, ContentStudioJson.Serialize(manifest) + Environment.NewLine);
                return manifest;
            }
            catch (Exception publicationException)
            {
                try
                {
                    RestorePreviousTarget(targetPath, backupPath, previousHash, expectedArtifactSha256);
                }
                catch (Exception restoreException)
                {
                    throw new ContentStudioException("Deployment failed and its previous target could not be restored safely. Stop publishing and recover the target from the recorded backup.", new AggregateException(publicationException, restoreException));
                }
                throw new ContentStudioException("Deployment failed. The previous target state was restored and no deployment was recorded.", publicationException);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Rollback(string targetPath, string backupPath)
    {
        var backup = RequireValidDatabase(backupPath);
        var target = Path.GetFullPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new ContentStudioException($"Unable to determine deployment directory for {target}.");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(targetDirectory, $".compact.rollback.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(backup, temporaryPath, true);
            File.Move(temporaryPath, target, true);
            RequireValidDatabase(target);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string RequireValidDatabase(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ContentStudioException($"Database does not exist: {fullPath}");
        }
        try
        {
            using var connection = CompactConnectionFactory.OpenReadOnly(fullPath);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
            if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new ContentStudioException($"SQLite integrity check failed for {fullPath}: {result}");
            }
        }
        catch (SqliteException exception)
        {
            throw new ContentStudioException($"Invalid SQLite database {fullPath}: {exception.Message}", exception);
        }
        return fullPath;
    }

    private static void RequireCompatibleSchema(string artifactPath, string targetPath)
    {
        var artifactSchema = ReadSchema(artifactPath);
        var targetSchema = ReadSchema(targetPath);
        if (artifactSchema.SetEquals(targetSchema)) return;

        var missing = targetSchema.Except(artifactSchema).Take(5).ToArray();
        var added = artifactSchema.Except(targetSchema).Take(5).ToArray();
        var details = new List<string>();
        if (missing.Length > 0) details.Add($"missing target entries: {string.Join(", ", missing)}");
        if (added.Length > 0) details.Add($"unexpected artifact entries: {string.Join(", ", added)}");
        throw new ContentStudioException($"The build artifact schema does not match the deployment target ({string.Join("; ", details)}). Build from the target's compatible baseline instead of replacing a different compact layout.");
    }

    private static void RestorePreviousTarget(
        string targetPath,
        string? backupPath,
        string? previousHash,
        string expectedDeployedHash)
    {
        if (!File.Exists(targetPath) ||
            !FileHashService.CalculateSha256(targetPath).Equals(expectedDeployedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStudioException("The deployment target changed again after publication; automatic recovery would overwrite another process's work.");
        }
        if (backupPath is null)
        {
            File.Delete(targetPath);
            if (File.Exists(targetPath)) throw new ContentStudioException("The newly created deployment target could not be removed.");
            return;
        }
        if (previousHash is null)
            throw new ContentStudioException("The previous deployment hash is missing, so its backup cannot be verified.");

        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new ContentStudioException($"Unable to determine deployment directory for {targetPath}.");
        var restorePath = Path.Combine(directory, $".compact.restore.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(backupPath, restorePath, false);
            RequireValidDatabase(restorePath);
            if (!FileHashService.CalculateSha256(restorePath).Equals(previousHash, StringComparison.OrdinalIgnoreCase))
                throw new ContentStudioException("The deployment backup no longer matches the previous target hash.");
            File.Move(restorePath, targetPath, true);
            RequireValidDatabase(targetPath);
            if (!FileHashService.CalculateSha256(targetPath).Equals(previousHash, StringComparison.OrdinalIgnoreCase))
                throw new ContentStudioException("The restored deployment target does not match its recorded previous hash.");
        }
        finally
        {
            if (File.Exists(restorePath)) File.Delete(restorePath);
        }
    }

    private static HashSet<string> ReadSchema(string path)
    {
        using var connection = CompactConnectionFactory.OpenReadOnly(path);
        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        using (var tablesReader = tablesCommand.ExecuteReader())
        {
            while (tablesReader.Read()) tables.Add(tablesReader.GetString(0));
        }

        var schema = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            schema.Add($"table:{table}");
            using var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = $"PRAGMA table_info({BaselineVerifier.QuoteIdentifier(table)});";
            using var columnsReader = columnsCommand.ExecuteReader();
            while (columnsReader.Read())
            {
                schema.Add($"column:{table}:{columnsReader.GetInt32(0)}:{columnsReader.GetString(1)}:{columnsReader.GetString(2)}:{columnsReader.GetInt32(3)}:{columnsReader.GetInt32(5)}");
            }
        }
        return schema;
    }
}
