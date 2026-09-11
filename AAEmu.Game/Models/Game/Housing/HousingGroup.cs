namespace AAEmu.Game.Models.Game.Housing;

public sealed class HousingGroup
{
    public uint Id { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public uint DoodadId { get; init; }
    public bool Houseless { get; init; }
    public uint ExistingCategoryId { get; init; }
    public int AllowedTaxDelayWeek { get; init; }
    public bool CanExtend { get; init; }
    public Dictionary<uint, uint> CategoryLimits { get; } = [];
}
