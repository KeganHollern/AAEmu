using System.ComponentModel.DataAnnotations;
using AAEmu.Commons.Models;

namespace AAEmu.Login.Models;

/// <summary>
/// Contains database connection configuration settings.
/// </summary>
public class DBConnectionsConfig
{
    public const string ConfigurationSectionName = "Connections";

    /// <summary>
    /// Gets or sets the MySQL database connection settings.
    /// </summary>
    [Required]
    public required MySqlConnectionSettings MySQLProvider { get; set; }

    /// <summary>
    /// Gets or sets whether to apply pending database updates without an interactive prompt.
    /// Set this to <c>true</c> for unattended environments such as containers, Aspire, or CI.
    /// When this is <c>false</c>, unattended startup fails if an update is pending.
    /// </summary>
    public bool AutoApplyUpdates { get; set; }
}
