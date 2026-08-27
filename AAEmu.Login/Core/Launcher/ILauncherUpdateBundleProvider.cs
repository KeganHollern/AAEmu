#nullable enable

namespace AAEmu.Login.Core.Launcher;

public sealed class LauncherUpdateAsset(
    string fileName,
    string sha256,
    long size,
    Func<Stream> openReadStream)
{
    public string FileName { get; } = fileName;
    public string Sha256 { get; } = sha256;
    public long Size { get; } = size;

    public Stream OpenReadStream() => openReadStream();
}

public interface ILauncherUpdateBundleProvider
{
    bool IsAvailable { get; }
    LauncherUpdateAsset Manifest { get; }
    LauncherUpdateAsset Minisig { get; }
    LauncherUpdateAsset LinuxArchive { get; }
    LauncherUpdateAsset WindowsArchive { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}
