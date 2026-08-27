namespace AAEmu.Login.Models;

public sealed class LauncherUpdateOptions
{
    public const string ConfigurationSectionName = "LauncherUpdate";

    public bool Enabled { get; set; }

    public string ReleasePath { get; set; } = string.Empty;

    public long ExpectedManifestSize { get; set; }

    public string ExpectedManifestSha256 { get; set; } = new('0', 64);

    public long ExpectedMinisigSize { get; set; }

    public string ExpectedMinisigSha256 { get; set; } = new('0', 64);

    public long ExpectedLinuxArchiveSize { get; set; }

    public string ExpectedLinuxArchiveSha256 { get; set; } = new('0', 64);

    public long ExpectedWindowsArchiveSize { get; set; }

    public string ExpectedWindowsArchiveSha256 { get; set; } = new('0', 64);
}
