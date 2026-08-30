using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.Game.Core.Managers.TowerDefense;

public interface ITowerDefenseManager : ILoadable, IInitializable, IDisposable
{
    IReadOnlyCollection<TowerDefenseOccurrence> GetActiveOccurrences();
    IReadOnlyList<string> GetEventDiagnostics();
    bool StartManual(string eventKeyOrTowerDefId, out string message);
    bool StartManual(string eventKeyOrTowerDefId, string siteKey, out string message);
    bool AdvanceManual(string eventKeyOrTowerDefId, out string message);
    bool EndManual(string eventKeyOrTowerDefId, string reason, out string message);
    void SendSnapshot(Character character);
    void HandleTimer(string occurrenceKey, int expectedGeneration, TowerDefenseTimerKind kind);
    void OnWorldsInitialized();
}

public enum TowerDefenseTimerKind
{
    FirstWave,
    StepTimer,
    HardDeadline
}
