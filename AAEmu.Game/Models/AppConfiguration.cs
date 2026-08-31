using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Expeditions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AAEmu.Game.Models;

public partial class AppConfiguration
{
    private static readonly AppConfiguration s_default = new();

    public static AppConfiguration Instance =>
        SingletonContainer.ServiceProvider?.GetService<IOptions<AppConfiguration>>()?.Value
        ?? s_default;

    public byte Id { get; set; }
    public byte[] AdditionalesId { get; set; } = [];
    public string SecretKey { get; set; }
    public DBConnections Connections { get; set; }
    public NetworkConfig Network { get; set; }
    public NetworkConfig StreamNetwork { get; set; }
    public NetworkConfig LoginNetwork { get; set; }
    public NetworkConfig WebApiNetwork { get; set; }
    public NetworkConfig HealthNetwork { get; set; }
    public string CharacterNameRegex { get; set; }
    public int MaxConcurencyThreadPool { get; set; }
    public bool HeightMapsEnable { get; set; }
    public string DiscordToken { get; set; }
    public ExpeditionConfig Expedition { get; set; }
    public WorldConfig World { get; set; }
    public DungeonsConfig Dungeons { get; set; } = new();
    public Dictionary<string, int> AccessLevel { get; set; } = [];
    public AccountConfig Account { get; set; }
    public CurrencyValuesConfig Labor { get; set; }
    public CurrencyValuesConfig LaborOffline { get; set; }
    public CurrencyValuesConfig Credits { get; set; }
    public CurrencyValuesConfig Loyalty { get; set; }
    public ClientDataConfig ClientData { get; set; } = new();
    public SpecialtyConfig Specialty { get; set; } = new();
    public ScriptsConfig Scripts { get; set; } = new();
    public JusticeConfig Justice { get; set; } = new();
    public AiChatConfig AiChat { get; set; } = new();
    public TrionWebConfig TrionWeb { get; set; } = new();
    public TowerDefenseConfig TowerDefense { get; set; } = new();
    public string DefaultLanguage { get; set; } = "en_us";
    public bool DebugInfo { get; set; } = true;
    public uint DebugInfoLevel { get; set; } = 100;

    public class NetworkConfig
    {
        public string Host { get; set; }
        public ushort Port { get; set; }
        public int NumConnections { get; set; }
    }

    /// <summary>
    /// Web URLs handed to the client at world-enter via SCTrionConfigPacket.
    /// The client treats each as a folder base (it appends its own paths) and
    /// renders the pages in its embedded Awesomium browser: the "Get Credits"
    /// popup, wiki, and web-shop surfaces.
    /// </summary>
    public class TrionWebConfig
    {
        public bool Activate { get; set; } = true;
        public string AuthUrl { get; set; } = "http://localhost/aaemu/login";
        public string PlatformUrl { get; set; } = "http://localhost/aaemu/platform";
        public string CommerceUrl { get; set; } = "http://localhost/aaemu/shop";
    }

    public class DBConnections
    {
        public MySqlConnectionSettings MySQLProvider { get; set; }

        /// <summary>
        /// Gets or sets whether to apply pending database updates without an interactive prompt.
        /// Set this to <c>true</c> for unattended environments such as containers, Aspire, or CI.
        /// When this is <c>false</c>, unattended startup fails if an update is pending.
        /// </summary>
        public bool AutoApplyUpdates { get; set; }
    }
}
