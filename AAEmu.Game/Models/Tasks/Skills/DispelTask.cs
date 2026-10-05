using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.Game.Models.Tasks.Skills;

public class DispelTask(Buff buff) : Task
{
    public WeakReference Effect = new(buff);

    public override void Execute()
    {
        if (!Effect.IsAlive)
            return;

        if (Effect.Target is not Buff eff || eff.IsEnded() || eff.Owner == null)
            return;

        // A removed NPC can remain referenced until its respawn timer fires.
        // Do not run its tick/timeout effects or schedule another task after removal.
        if (eff.Owner is Npc { Despawned: true } or Npc { CombatRetired: true })
            return;

        eff.ScheduleEffect(false);

        if (eff.IsEnded())
        {
            return;
        }
        EffectTaskManager.Instance.AddDispelTask(eff, eff.Tick);
    }
}
