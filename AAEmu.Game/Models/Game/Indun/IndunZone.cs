namespace AAEmu.Game.Models.Game.Indun;

public class IndunZone
{
    /// <summary>
    /// ZoneGroupId for this dungeon
    /// </summary>
    public uint ZoneGroupId { get; init; }
    /// <summary>
    /// Dungeon nam
    /// </summary>
    public string Name { get; init; }
    /// <summary>
    /// Dungeon comments
    /// </summary>
    public string Comment { get; init; }
    /// <summary>
    /// Minimum character level required to enter this dungeon
    /// </summary>
    public uint LevelMin { get; init; } = 1;
    /// <summary>
    /// Maximum level a character can have to enter this dungeon
    /// </summary>
    public uint LevelMax { get; init; } = 100;
    /// <summary>
    /// Maximum number of players in this dungeon
    /// </summary>
    public uint MaxPlayers { get; init; } = 9999;
    /// <summary>
    /// Set to false, then PvP is NOT allowed in this dungeon (used for Mirage and Library)
    /// </summary>
    public bool PvP { get; init; } = true;
    /// <summary>
    /// If this dungeon has its own respawn points, not sure how this is used
    /// </summary>
    public bool HasGraveyard { get; init; } = true;
    /// <summary>
    /// If set, the itemTemplateId of the item required/consumed to enter this dungeon
    /// </summary>
    public uint ItemId { get; init; }
    /// <summary>
    /// Legacy per-zone item restoration interval from the compact data.
    /// Player-created dungeon throttling is configured in <see cref="DungeonsConfig"/>.
    /// </summary>
    public uint RestoreItemTime { get; init; }
    /// <summary>
    /// Does the player need to be part of a party in order to enter this dungeon
    /// </summary>
    public bool PartyOnly { get; init; }
    /// <summary>
    /// Is this dungeon only run clients-side
    /// </summary>
    public bool ClientDriven { get; init; }
    /// <summary>
    /// Does this dungeon have a channel select
    /// </summary>
    public bool SelectChannel { get; init; }

    /// <summary>
    /// Cached localized name of this dungeon
    /// </summary>
    public string LocalizedName { get; set; }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(LocalizedName) ? Name : LocalizedName;
    }
}
