#nullable enable

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using AAEmu.Commons.IO;
using AAEmu.Login.Models;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace AAEmu.Login.Core.Launcher;

public sealed class ClientContentBundleProvider(
    IOptions<LauncherApiOptions> options,
    ILogger<ClientContentBundleProvider> logger) : IClientContentBundleProvider, IDisposable
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxMinisigBytes = 16 * 1024;
    private const int MaxArtifacts = 8;
    private const long MaxFullBlobBytes = 1024L * 1024 * 1024;
    private const long MaxSparseBlobBytes = MaxFullBlobBytes + MaxManifestBytes + 24;
    private readonly LauncherApiOptions _options = options.Value;
    private BundleSnapshot? _snapshot;

    public bool IsAvailable => _snapshot is not null;

    public ReadOnlyMemory<byte> ManifestBytes => Snapshot.ManifestBytes;

    public ReadOnlyMemory<byte> MinisigBytes => Snapshot.MinisigBytes;

    public string ManifestSha256 => Snapshot.ManifestSha256;

    public string MinisigSha256 => Snapshot.MinisigSha256;

    private BundleSnapshot Snapshot => _snapshot
        ?? throw new InvalidOperationException("The launcher content bundle has not been initialized");

    public bool TryGetAsset(string sha256, [NotNullWhen(true)] out ClientContentAsset? asset)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            asset = null;
            return false;
        }

        return snapshot.Assets.TryGetValue(sha256, out asset);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _snapshot, null)?.Dispose();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var releasePath = ResolveReleasePath(_options.ContentV2.ReleasePath);
        EnsureRegularDirectory(releasePath, "release directory");
        var blobsPath = Path.Combine(releasePath, "blobs");
        EnsureRegularDirectory(blobsPath, "release blob directory");

        var manifestPath = Path.Combine(releasePath, "manifest.json");
        var minisigPath = Path.Combine(releasePath, "manifest.minisig");
        var manifestBytes = await ReadBoundedFileAsync(
            manifestPath, MaxManifestBytes, "release manifest", cancellationToken);
        var minisigBytes = await ReadBoundedFileAsync(
            minisigPath, MaxMinisigBytes, "release signature", cancellationToken);

        ValidateManifestEnvelope(manifestBytes);
        var manifestSha256 = Sha256(manifestBytes);
        var minisigSha256 = Sha256(minisigBytes);
        RequireExpectedDigest(manifestSha256, _options.ContentV2.ExpectedManifestSha256, "manifest");
        RequireExpectedDigest(minisigSha256, _options.ContentV2.ExpectedMinisigSha256, "signature");

        var declaredAssets = ParseAssetCatalog(manifestBytes);
        ValidateBlobDirectoryClosure(blobsPath, declaredAssets.Keys);
        var verifiedAssets = new Dictionary<string, ClientContentAsset>(StringComparer.Ordinal);
        var verifiedFiles = new List<FileStream>();
        try
        {
            foreach (var (sha256, size) in declaredAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(blobsPath, sha256);
                var fileInfo = EnsureRegularFile(path, $"release blob {sha256}");
                if (fileInfo.Length != size)
                {
                    throw new InvalidOperationException(
                        $"Release blob {sha256} size {fileInfo.Length} does not match {size}");
                }

                var verifiedFile = OpenAssetFile(path, size);
                verifiedFiles.Add(verifiedFile);
                var actualSha256 = await HashExactFileAsync(verifiedFile, size, cancellationToken);
                if (!actualSha256.Equals(sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Release blob {sha256} SHA-256 mismatch");
                verifiedAssets.Add(sha256, CreateAsset(sha256, size, verifiedFile));
            }

            _snapshot = new BundleSnapshot(
                manifestBytes,
                minisigBytes,
                manifestSha256,
                minisigSha256,
                verifiedAssets.ToFrozenDictionary(StringComparer.Ordinal),
                verifiedFiles);
        }
        catch
        {
            foreach (var verifiedFile in verifiedFiles)
                verifiedFile.Dispose();
            throw;
        }
        logger.LogInformation(
            "Launcher v2 content bundle verified: manifest SHA-256 {Sha256}, {AssetCount} assets",
            manifestSha256, verifiedAssets.Count);
    }

    private static string ResolveReleasePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("Launcher v2 release path is empty");
        var parts = configuredPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("..", StringComparer.Ordinal))
            throw new InvalidOperationException("Launcher v2 release path must not contain '..'");
        var candidate = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(FileManager.AppPath, configuredPath);
        return Path.GetFullPath(candidate);
    }

    private static void EnsureRegularDirectory(string path, string name)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"The configured {name} is missing or is a reparse point");
        EnsureNoReparseAncestors(directory);
    }

    private static FileInfo EnsureRegularFile(string path, string name)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.Directory)
                         || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"The configured {name} is missing or is not a regular file");
        if (file.Directory is not null)
            EnsureNoReparseAncestors(file.Directory);
        return file;
    }

    private static void EnsureNoReparseAncestors(DirectoryInfo directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"Launcher v2 content path contains a reparse point: {current.FullName}");
            }
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        string name,
        CancellationToken cancellationToken)
    {
        var fileInfo = EnsureRegularFile(path, name);
        if (fileInfo.Length is < 1 || fileInfo.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                $"The configured {name} size {fileInfo.Length} is outside 1..{maximumBytes} bytes");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != fileInfo.Length)
            throw new InvalidOperationException($"The configured {name} changed before it was read");
        var bytes = new byte[checked((int)fileInfo.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1)
            throw new InvalidOperationException($"The configured {name} grew while it was read");
        return bytes;
    }

    private static void ValidateManifestEnvelope(byte[] manifestBytes)
    {
        if (manifestBytes[^1] != (byte)'\n')
            throw new InvalidOperationException("Launcher v2 manifest must end in exactly one LF");
        for (var index = 0; index < manifestBytes.Length; index++)
        {
            var value = manifestBytes[index];
            if (value > 0x7f || value == (byte)'\r'
                             || (value == (byte)'\n' && index != manifestBytes.Length - 1))
            {
                throw new InvalidOperationException(
                    "Launcher v2 manifest must be single-line ASCII followed by one LF");
            }
        }
    }

    private static Dictionary<string, long> ParseAssetCatalog(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(
            manifestBytes.AsMemory(0, manifestBytes.Length - 1),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Launcher v2 manifest root must be an object");
        EnsureUniqueProperties(root, "manifest");
        RequireInteger(root, "schemaVersion", 2);
        RequireString(root, "product", "aaemu-r208022-client");
        RequireString(root, "channel", "production");
        RequireInteger(root, "launcherProtocolFloor", 2);

        var artifacts = RequireProperty(root, "artifacts");
        if (artifacts.ValueKind != JsonValueKind.Array
            || artifacts.GetArrayLength() is < 4 or > MaxArtifacts)
        {
            throw new InvalidOperationException("Launcher v2 manifest must contain 4..8 artifacts");
        }

        var assets = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var artifact in artifacts.EnumerateArray())
        {
            var representation = RequireProperty(artifact, "representation");
            var kind = RequireProperty(representation, "kind").GetString();
            var blob = RequireProperty(representation, "blob");
            var sha256 = RequireProperty(blob, "sha256").GetString();
            if (!IsLowerSha256(sha256))
                throw new InvalidOperationException("Launcher v2 blob SHA-256 must be lowercase hexadecimal");
            if (!RequireProperty(blob, "size").TryGetInt64(out var size) || size < 1)
                throw new InvalidOperationException("Launcher v2 blob size must be a positive integer");
            var maximum = kind switch
            {
                "full" => MaxFullBlobBytes,
                "sparse-v1" => MaxSparseBlobBytes,
                _ => throw new InvalidOperationException("Launcher v2 representation kind is unsupported")
            };
            if (size > maximum)
                throw new InvalidOperationException($"Launcher v2 {kind} blob exceeds its size limit");
            if (assets.TryGetValue(sha256!, out var priorSize) && priorSize != size)
                throw new InvalidOperationException("Launcher v2 blob has conflicting declared sizes");
            assets[sha256!] = size;
        }

        if (assets.Count is < 1 or > MaxArtifacts)
            throw new InvalidOperationException("Launcher v2 manifest has an invalid distinct blob count");
        return assets;
    }

    private static void EnsureUniqueProperties(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidOperationException($"Duplicate JSON property at {path}.{property.Name}");
                    EnsureUniqueProperties(property.Value, $"{path}.{property.Name}");
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    EnsureUniqueProperties(item, $"{path}[{index++}]");
                break;
        }
    }

    private static JsonElement RequireProperty(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
            throw new InvalidOperationException($"Launcher v2 manifest is missing {name}");
        return property;
    }

    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (RequireProperty(value, name).GetString() != expected)
            throw new InvalidOperationException($"Launcher v2 manifest {name} is unsupported");
    }

    private static void RequireInteger(JsonElement value, string name, int expected)
    {
        var property = RequireProperty(value, name);
        if (!property.TryGetInt32(out var actual) || actual != expected)
            throw new InvalidOperationException($"Launcher v2 manifest {name} is unsupported");
    }

    private static bool IsLowerSha256(string? value)
    {
        return value is { Length: 64 } && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
    }

    private static void ValidateBlobDirectoryClosure(string blobsPath, IEnumerable<string> expectedNames)
    {
        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        var actual = Directory.EnumerateFileSystemEntries(blobsPath)
            .Take(MaxArtifacts + 1)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw new InvalidOperationException("Launcher v2 blob directory does not match the manifest");
    }

    private static FileStream OpenAssetFile(string path, long expectedSize)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length == expectedSize)
            return stream;
        stream.Dispose();
        throw new InvalidOperationException("Launcher v2 blob changed before it was opened");
    }

    private static async Task<string> HashExactFileAsync(
        FileStream stream, long expectedSize, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var remaining = expectedSize;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
                throw new InvalidOperationException("Launcher v2 blob ended while hashing");
            digest.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidOperationException("Launcher v2 blob grew while hashing");
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static ClientContentAsset CreateAsset(string sha256, long size, FileStream verifiedFile) =>
        new(sha256, size, () => new VerifiedAssetReadStream(verifiedFile, size));

    private static void RequireExpectedDigest(string actual, string expected, string name)
    {
        if (!actual.Equals(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Launcher v2 {name} SHA-256 does not match its pin");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record BundleSnapshot(
        byte[] ManifestBytes,
        byte[] MinisigBytes,
        string ManifestSha256,
        string MinisigSha256,
        FrozenDictionary<string, ClientContentAsset> Assets,
        List<FileStream> VerifiedFiles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var verifiedFile in VerifiedFiles)
                verifiedFile.Dispose();
        }
    }

    private sealed class VerifiedAssetReadStream(FileStream owner, long length) : Stream
    {
        private readonly SafeFileHandle _handle = owner.SafeFileHandle;
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var available = Available(buffer.Length);
            if (available == 0)
                return 0;
            var read = RandomAccess.Read(_handle, buffer[..available], _position);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var available = Available(buffer.Length);
            return available == 0
                ? ValueTask.FromResult(0)
                : ReadAsyncCore(buffer[..available], cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0)
                throw new IOException("Cannot seek before the start of the launcher content asset");
            _position = position;
            return position;
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        private int Available(int requested) =>
            (int)Math.Min(requested, Math.Max(0, length - _position));

        private async ValueTask<int> ReadAsyncCore(
            Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await RandomAccess.ReadAsync(_handle, buffer, _position, cancellationToken);
            _position += read;
            GC.KeepAlive(owner);
            return read;
        }
    }
}
