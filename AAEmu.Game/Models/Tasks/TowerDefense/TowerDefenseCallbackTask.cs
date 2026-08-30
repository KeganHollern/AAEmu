using AAEmu.Game.Core.Managers.TowerDefense;

namespace AAEmu.Game.Models.Tasks.TowerDefense;

public sealed class TowerDefenseCallbackTask : Task
{
    private readonly ITowerDefenseManager _manager;
    private readonly string _occurrenceKey;
    private readonly int _expectedGeneration;

    public TowerDefenseTimerKind Kind { get; }

    public TowerDefenseCallbackTask(
        ITowerDefenseManager manager,
        string occurrenceKey,
        int expectedGeneration,
        TowerDefenseTimerKind kind)
    {
        _manager = manager;
        _occurrenceKey = occurrenceKey;
        _expectedGeneration = expectedGeneration;
        Kind = kind;
    }

    public override void Execute() => _manager.HandleTimer(_occurrenceKey, _expectedGeneration, Kind);
}
