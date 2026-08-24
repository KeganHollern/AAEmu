using System.ComponentModel.DataAnnotations;

namespace AAEmu.Login.Models;

public sealed class LauncherApiOptions
{
    public const string ConfigurationSectionName = "LauncherApi";

    public bool Enabled { get; set; }

    [Range(1, 1440)]
    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    [Range(30, 600)]
    public int LaunchTicketLifetimeSeconds { get; set; } = 180;

    public LauncherContentV2Options ContentV2 { get; set; } = new();
}

public sealed class LauncherContentV2Options
{
    public string ReleasePath { get; set; } = string.Empty;

    public string ExpectedManifestSha256 { get; set; } = new('0', 64);

    public string ExpectedMinisigSha256 { get; set; } = new('0', 64);
}
