using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers;

public interface ISaveManager : IInitializable
{
    System.Threading.Tasks.Task StopAsync();
    void SaveTickStart();
    bool DoSave();
    bool TryCommitEconomy(IReadOnlyCollection<Character> participants, Action<PersistenceSaveContext> writeSettlement = null);
}
