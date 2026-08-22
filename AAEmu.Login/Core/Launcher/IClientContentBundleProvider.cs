#nullable enable

using System.Diagnostics.CodeAnalysis;

namespace AAEmu.Login.Core.Launcher;

public sealed class ClientContentAsset(
    string sha256,
    long size,
    Func<Stream> openReadStream)
{
    public string Sha256 { get; } = sha256;
    public long Size { get; } = size;

    public Stream OpenReadStream() => openReadStream();
}

public interface IClientContentBundleProvider
{
    bool IsAvailable { get; }
    ReadOnlyMemory<byte> ManifestBytes { get; }
    ReadOnlyMemory<byte> MinisigBytes { get; }
    string ManifestSha256 { get; }
    string MinisigSha256 { get; }
    bool TryGetAsset(string sha256, [NotNullWhen(true)] out ClientContentAsset? asset);
    Task InitializeAsync(CancellationToken cancellationToken);
}
