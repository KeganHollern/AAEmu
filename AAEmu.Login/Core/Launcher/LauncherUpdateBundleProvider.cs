#nullable enable

using System.Security.Cryptography;
using AAEmu.Commons.IO;
using AAEmu.Login.Models;
using Microsoft.Extensions.Options;

namespace AAEmu.Login.Core.Launcher;

public sealed class LauncherUpdateBundleProvider(
    IOptions<LauncherUpdateOptions> options,
    ILogger<LauncherUpdateBundleProvider> logger) : ILauncherUpdateBundleProvider
{
    public const string ManifestFileName = "manifest.json";
    public const string MinisigFileName = "manifest.minisig";
    public const string LinuxArchiveFileName = "aaemu-launcher-linux-x86_64.tar.gz";
    public const string WindowsArchiveFileName = "aaemu-launcher-windows-i686.zip";

    private readonly LauncherUpdateOptions _options = options.Value;
    private BundleSnapshot? _snapshot;

    public bool IsAvailable => _snapshot is not null;

    public LauncherUpdateAsset Manifest => Snapshot.Manifest;

    public LauncherUpdateAsset Minisig => Snapshot.Minisig;

    public LauncherUpdateAsset LinuxArchive => Snapshot.LinuxArchive;

    public LauncherUpdateAsset WindowsArchive => Snapshot.WindowsArchive;

    private BundleSnapshot Snapshot => _snapshot
        ?? throw new InvalidOperationException("The launcher update bundle has not been initialized");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var releasePath = ResolveReleasePath(_options.ReleasePath);
        EnsureRegularDirectory(releasePath);

        var manifest = await VerifyAssetAsync(
            releasePath,
            ManifestFileName,
            _options.ExpectedManifestSize,
            _options.ExpectedManifestSha256,
            cancellationToken);
        var minisig = await VerifyAssetAsync(
            releasePath,
            MinisigFileName,
            _options.ExpectedMinisigSize,
            _options.ExpectedMinisigSha256,
            cancellationToken);
        var linuxArchive = await VerifyAssetAsync(
            releasePath,
            LinuxArchiveFileName,
            _options.ExpectedLinuxArchiveSize,
            _options.ExpectedLinuxArchiveSha256,
            cancellationToken);
        var windowsArchive = await VerifyAssetAsync(
            releasePath,
            WindowsArchiveFileName,
            _options.ExpectedWindowsArchiveSize,
            _options.ExpectedWindowsArchiveSha256,
            cancellationToken);

        _snapshot = new BundleSnapshot(manifest, minisig, linuxArchive, windowsArchive);
        logger.LogInformation(
            "Launcher update bundle verified: manifest SHA-256 {Sha256}",
            manifest.Sha256);
    }

    private static string ResolveReleasePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("Launcher update release path is empty");
        var parts = configuredPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("..", StringComparer.Ordinal))
            throw new InvalidOperationException("Launcher update release path must not contain '..'");
        var candidate = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(FileManager.AppPath, configuredPath);
        return Path.GetFullPath(candidate);
    }

    private static void EnsureRegularDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "The configured launcher update release directory is missing or is a reparse point");
        }
    }

    private static async Task<LauncherUpdateAsset> VerifyAssetAsync(
        string releasePath,
        string fileName,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(releasePath, fileName);
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.Directory)
                         || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"The configured launcher update file {fileName} is missing or is not a regular file");
        }
        if (file.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"Launcher update file {fileName} size {file.Length} does not match {expectedSize}");
        }

        await using var stream = OpenReadStream(path, expectedSize, fileName);
        var actualSha256 = await HashExactFileAsync(stream, expectedSize, fileName, cancellationToken);
        if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"Launcher update file {fileName} SHA-256 does not match its pin");

        return new LauncherUpdateAsset(
            fileName,
            actualSha256,
            expectedSize,
            () => OpenReadStream(path, expectedSize, fileName));
    }

    private static FileStream OpenReadStream(string path, long expectedSize, string fileName)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length == expectedSize)
            return stream;
        stream.Dispose();
        throw new InvalidOperationException($"Launcher update file {fileName} size changed");
    }

    private static async Task<string> HashExactFileAsync(
        FileStream stream,
        long expectedSize,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var remaining = expectedSize;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
                throw new InvalidOperationException($"Launcher update file {fileName} ended while hashing");
            digest.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidOperationException($"Launcher update file {fileName} grew while hashing");
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private sealed record BundleSnapshot(
        LauncherUpdateAsset Manifest,
        LauncherUpdateAsset Minisig,
        LauncherUpdateAsset LinuxArchive,
        LauncherUpdateAsset WindowsArchive);
}
