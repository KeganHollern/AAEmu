using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.Units;

public partial class Buffs
{
    private readonly HashSet<Buff> _specialEffectClaims = [];

    public bool TryConsumeActiveBuff(Buff buff)
    {
        lock (_lock)
        {
            if (!_effects.Contains(buff) || !buff.InUse || buff.State != EffectState.Acting ||
                !_specialEffectClaims.Add(buff))
                return false;
        }

        try
        {
            // The claim decides which special effect owns consumption. Existing
            // timers can still expire the buff, so reject an observed expiry.
            if (!buff.InUse || buff.State != EffectState.Acting || buff.GetTimeLeft() == 0)
                return false;

            // Exit can take Buff's own lock and call back into this collection.
            // Never hold the collection lock across that lifecycle operation.
            buff.Exit();
            return true;
        }
        finally
        {
            lock (_lock)
                _specialEffectClaims.Remove(buff);
        }
    }
}
