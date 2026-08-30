using Newtonsoft.Json;

namespace AAEmu.Game.Models.Game.TowerDefs;

public sealed class TowerDefenseManifest
{
    public int SchemaVersion { get; set; }
    public List<TowerDefenseEventManifest> Events { get; set; } = [];
}

public sealed class TowerDefenseEventManifest
{
    public string Key { get; set; }
    public uint TowerDefId { get; set; }
    public bool Enabled { get; set; }
    public string WorldTemplate { get; set; } = "main_world";
    public uint ZoneGroupId { get; set; }
    public TowerDefenseTriggerManifest Trigger { get; set; } = new();
    public string ConcurrencyGroup { get; set; }
    public string RestartPolicy { get; set; } = "RestartCurrentStep";
    public bool ImmediateTransitionAllowed { get; set; }
    public List<TowerDefenseSiteManifest> Sites { get; set; } = [];
}

public sealed class TowerDefenseTriggerManifest
{
    public string Type { get; set; } = "TimeOfDay";
    public float Hour { get; set; }
    public uint DayInterval { get; set; } = 1;
    public uint DayPhase { get; set; }
    public uint CatchUpGraceSeconds { get; set; } = 30;
}

public sealed class TowerDefenseSiteManifest
{
    public string Key { get; set; }
    public uint SpotId { get; set; }
    [JsonIgnore]
    public uint EventZoneId { get; set; }
    public TowerDefenseAnchor Anchor { get; set; } = new();
    public Dictionary<string, List<string>> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TowerDefenseAnchor
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
