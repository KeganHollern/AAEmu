using AAEmu.ContentStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ContentStudio.Core.Services;

public sealed class DeploymentService
{
    public DeploymentManifest Deploy(string artifactPath, string targetName, DeploymentTarget target, string manifestDirectory)
    {
        var artifact = RequireValidDatabase(artifactPath);
        var targetPath = Path.GetFullPath(target.Path);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new ContentStudioException($"Unable to determine deployment directory for {targetPath}.");
        Directory.CreateDirectory(targetDirectory);

        string? previousHash = null;
        string? backupPath = null;
        if (File.Exists(targetPath))
        {
            RequireValidDatabase(targetPath);
            previousHash = FileHashService.CalculateSha256(targetPath);
            var backupDirectory = Path.GetFullPath(target.BackupDirectory);
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, $"compact.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.{previousHash[..12]}.sqlite3");
            File.Copy(targetPath, backupPath, false);
        }

        var temporaryPath = Path.Combine(targetDirectory, $".compact.deploy.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(artifact, temporaryPath, true);
            var artifactHash = FileHashService.CalculateSha256(artifact);
            if (!FileHashService.CalculateSha256(temporaryPath).Equals(artifactHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ContentStudioException("The staged deployment hash does not match the build artifact.");
            }
            File.Move(temporaryPath, targetPath, true);
            RequireValidDatabase(targetPath);

            var manifest = new DeploymentManifest
            {
                DeployedAtUtc = DateTimeOffset.UtcNow,
                TargetName = targetName,
                TargetPath = targetPath,
                ArtifactPath = artifact,
                ArtifactSha256 = artifactHash,
                PreviousSha256 = previousHash,
                BackupPath = backupPath
            };
            Directory.CreateDirectory(manifestDirectory);
            var manifestPath = Path.Combine(manifestDirectory, $"deployment-{targetName}-{manifest.DeployedAtUtc:yyyyMMdd-HHmmss}.json");
            AtomicFile.WriteAllText(manifestPath, ContentStudioJson.Serialize(manifest) + Environment.NewLine);
            return manifest;
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
}
