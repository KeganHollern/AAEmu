using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Duels;

namespace AAEmu.Game.Models.Tasks.Duels;

public class DuelStartTask(Duel duel) : Task
{
    public override void Execute() => DuelManager.Instance.DuelStart(duel);
}
