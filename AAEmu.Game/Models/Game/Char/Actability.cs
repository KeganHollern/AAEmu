using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Char.Templates;

namespace AAEmu.Game.Models.Game.Char;

public class Actability(ActabilityTemplate template)
{
    public uint Id { get; init; } = template.Id;
    public ActabilityTemplate Template { get; set; } = template;
    public int Point { get; set; }
    public byte Step { get; set; }

    /// <summary>Gets the authored character experience bonus for the current unlocked rank.</summary>
    public float GetExpMultiplier()
    {
        var limit = CharacterManager.Instance.GetExpertLimit(Step);
        return 1f + (limit?.ExpMultiplier ?? 0) / 100f;
    }

    /// <summary>Gets the remaining labor cost after the authored percentage reduction.</summary>
    public float GetLaborCostMultiplier()
    {
        var limit = CharacterManager.Instance.GetExpertLimit(Step);
        return 1f - (limit?.Advantage ?? 0) / 100f;
    }

    /// <summary>Gets the remaining production time after the authored percentage reduction.</summary>
    public float GetProductionTimeMultiplier()
    {
        var limit = CharacterManager.Instance.GetExpertLimit(Step);
        return 1f - (limit?.CastAdvantage ?? 0) / 100f;
    }

    // exp_mul controls character experience. The rank table does not author a loot bonus.
    public float GetLootMultiplier() => 1f;
}
