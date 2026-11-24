using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    protected override int GetMinimumHealthAfterDamage(BaseUnit attacker, KillReason killReason) =>
        killReason != KillReason.Gm && DuelManager.Instance.IsNonlethalOpponentDamage(attacker, this) ? 1 : 0;

    internal void ComputeDeathWaitTime(DateTime now, KillReason killReason, ResurrectionGameData data)
    {
        // These fields already persist with the character. Reconnects retain the
        // same escalation and deadline instead of starting a separate counter.
        var previous = data.Get(DeadCount);
        var withinPenalty = DeadCount > 0 && DeadTime <= now &&
                            (now - DeadTime).TotalMilliseconds < previous.PenaltyMilliseconds;
        var nextCount = withinPenalty ? DeadCount + 1 : 1;
        var row = data.Get(nextCount);
        DeadCount = checked((short)row.DeathCount);
        DeadTime = now;
        RezWaitDuration = killReason == KillReason.PvpSiege ? row.SiegeWaitMilliseconds : row.WaitMilliseconds;
        RezPenaltyDuration = row.PenaltyMilliseconds;
    }

    internal bool TryBeginResurrection(bool inPlace, DateTime now, out ResurrectionOffer offer)
    {
        lock (StorePurchaseSyncRoot)
        {
            offer = null;
            if (!CanResurrect(now) || inPlace && !TryTakeResurrectionOffer(out offer, now))
                return false;
            ClearResurrectionOffer();
            RezTime = now;
            return true;
        }
    }

    internal int GetRemainingResurrectionWait(DateTime now)
    {
        if (Hp > 0)
            return 0;
        var remainingMilliseconds = Math.Max(0, RezWaitDuration) - (now - DeadTime).TotalMilliseconds;
        return (int)Math.Clamp(Math.Ceiling(remainingMilliseconds), 0, int.MaxValue);
    }

    internal bool CanResurrect(DateTime now) => Hp <= 0 && GetRemainingResurrectionWait(now) == 0;
}
