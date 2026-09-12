namespace AAEmu.Game.Models.Game.TowerDefs;

public sealed record TowerDefenseSpawnToken(
    string OccurrenceKey,
    string EventKey,
    string SiteKey,
    int Generation,
    int StepOrdinal,
    string ActionKey,
    uint CreatorObjId = 0,
    bool DespawnOnCreatorDeath = false)
{
    // Record copies made by summon effects share the placement's lifetime.
    public TowerDefenseSpawnLifetime Lifetime { get; } = new();
}

public sealed class TowerDefenseSpawnLifetime
{
    private readonly object _sync = new();
    private volatile bool _cancelled;

    public bool IsCancelled => _cancelled;

    public void Cancel()
    {
        lock (_sync)
            _cancelled = true;
    }

    internal bool TryRegister(Func<bool> register)
    {
        lock (_sync)
            return !_cancelled && register();
    }
}
