using System.Security.Cryptography;
using AAEmu.Commons.IO;
using AAEmu.Login.Models;
using Microsoft.Extensions.Options;

namespace AAEmu.Login.Core.Launcher;

public sealed class ClientCompactProvider(
    IOptions<LauncherApiOptions> options,
    ILogger<ClientCompactProvider> logger) : IClientCompactProvider
{
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
    private readonly LauncherApiOptions _options = options.Value;
    private ClientCompactManifestResponse? _manifest;

    public bool IsAvailable => _manifest is not null;

    public string FilePath { get; private set; } = string.Empty;

    public ClientCompactManifestResponse Manifest => _manifest
        ?? throw new InvalidOperationException("The client compact has not been initialized");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var configuredPath = _options.ClientCompactPath;
        var candidate = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(FileManager.AppPath, configuredPath);
        FilePath = Path.GetFullPath(candidate);

        var fileInfo = new FileInfo(FilePath);
        if (!fileInfo.Exists || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The configured launcher client compact is missing or is a symlink");
        if (fileInfo.Length != _options.ExpectedClientCompactSize)
        {
            throw new InvalidOperationException(
                $"The launcher client compact size {fileInfo.Length} does not match " +
                $"{_options.ExpectedClientCompactSize}");
        }

        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[SqliteHeader.Length];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.SequenceEqual(SqliteHeader))
            throw new InvalidOperationException("The configured launcher client compact is not a SQLite database");

        stream.Position = 0;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualSha256 = Convert.ToHexStringLower(digest);
        if (!actualSha256.Equals(_options.ExpectedClientCompactSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The launcher client compact SHA-256 {actualSha256} does not match " +
                $"{_options.ExpectedClientCompactSha256}");
        }

        _manifest = new ClientCompactManifestResponse(
            SchemaVersion: 1,
            ContentVersion: actualSha256,
            Sha256: actualSha256,
            Size: fileInfo.Length,
            DownloadPath: "/launcher/v1/assets/client.sqlite3");
        logger.LogInformation("Launcher client compact verified: {Size} bytes, SHA-256 {Sha256}",
            fileInfo.Length, actualSha256);
    }
}
