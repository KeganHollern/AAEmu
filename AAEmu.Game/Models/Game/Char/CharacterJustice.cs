using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    // Positive values retain the stored unit (minutes). -1 represents a pending zero-minute
    // case; 0 remains "no case" for existing databases, whose region defaults to Nuian.
    public bool HasPendingTrial => OfflineGuiltyTime != 0;
    public bool IsPrisoner => HasPendingTrial || Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner);

    internal Character GetArrestorOnPlayerDeath(BaseUnit killer, bool pirateDesperadoZone)
    {
        var arrestor = killer?.GetOwnerCharacter();
        if (arrestor == null || arrestor.Id == Id)
            return null;
        // A stale runtime object ID must not attribute a pet/vehicle kill to a new owner.
        if (killer is AAEmu.Game.Models.Game.Units.Mate mate && mate.OwnerId != arrestor.Id ||
            killer is Slave slave && (slave.Summoner?.Id ?? slave.OwnerId) != arrestor.Id)
            return null;
        if (IsPrisoner)
            return arrestor;
        return Buffs.CheckBuffTag((uint)BuffConstants.TagWanted) &&
            (!Buffs.CheckBuff((uint)BuffConstants.Contemptuous) || !pirateDesperadoZone) ? arrestor : null;
    }

    internal void SetPendingTrialSentence(int minutes, CourtRoomRegion region)
    {
        OfflineGuiltyTime = minutes > 0 ? minutes : -1;
        OfflineGuiltyRegion = region;
    }

    internal void ClearPendingTrialSentence()
    {
        OfflineGuiltyTime = 0;
        OfflineGuiltyRegion = CourtRoomRegion.Invalid;
    }

    internal CourtRoomRegion GetPrisonCourtRegion() =>
        Buffs.CheckBuff((uint)BuffConstants.Prisoner_Nuian) ? CourtRoomRegion.Nuian :
        Buffs.CheckBuff((uint)BuffConstants.Prisoner_Haranyan) ? CourtRoomRegion.Haranyan : CourtRoomRegion.Invalid;

    internal int GetUnservedPrisonMilliseconds()
    {
        var nuian = Buffs.GetEffectFromBuffId((uint)BuffConstants.Prisoner_Nuian);
        var haranyan = Buffs.GetEffectFromBuffId((uint)BuffConstants.Prisoner_Haranyan);
        var bot = Buffs.GetEffectFromBuffId((uint)BuffConstants.Prisoner_Bot);
        var remaining = Math.Max(Math.Max(nuian?.GetTimeLeft() ?? 0, haranyan?.GetTimeLeft() ?? 0),
            bot?.GetTimeLeft() ?? 0);
        return (int)Math.Clamp(remaining, 0, int.MaxValue);
    }

    internal static int CombinePrisonSentence(int newMinutes, int unservedMilliseconds) =>
        (int)Math.Clamp(Math.Max(10000L, Math.Max(0L, newMinutes) * 60000L) +
            Math.Max(0, unservedMilliseconds), 10000L, int.MaxValue);

    internal bool ApplyPrisonSentence(CourtRoomRegion region, int newMinutes)
    {
        var buffId = region switch
        {
            CourtRoomRegion.Nuian => (uint)BuffConstants.Prisoner_Nuian,
            CourtRoomRegion.Haranyan => (uint)BuffConstants.Prisoner_Haranyan,
            _ => 0u
        };
        if (buffId == 0)
            return false;
        var duration = CombinePrisonSentence(newMinutes, GetUnservedPrisonMilliseconds());
        Buffs.AddBuff(buffId, this, duration);
        if (!Buffs.CheckBuff(buffId))
            return false;
        Buffs.RemoveBuff(buffId == (uint)BuffConstants.Prisoner_Nuian
            ? (uint)BuffConstants.Prisoner_Haranyan : (uint)BuffConstants.Prisoner_Nuian);
        Buffs.RemoveBuff((uint)BuffConstants.Prisoner_Bot);
        ClearPendingTrialSentence();
        return true;
    }
}
