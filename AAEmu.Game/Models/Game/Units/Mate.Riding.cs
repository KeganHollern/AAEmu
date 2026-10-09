using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.Units;

public sealed partial class Mate
{
    private readonly RidingMileage _ridingMileage = new();

    internal void ResetRidingMovement()
    {
        lock (SaveManager.PersistenceSyncRoot)
            _ridingMileage.ResetMovement();
    }

    internal int RecordRidingMileage(Character author, Vector3 previousWorldPosition,
        bool sameWorldFrame, bool isSkillController, DateTime observedAt)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (author == null)
            {
                _ridingMileage.ResetMovement();
                return 0;
            }

            lock (author.AttachmentSyncRoot)
            lock (AttachmentSyncRoot)
            {
                var eligible = sameWorldFrame && !isSkillController && Transform.Parent == null &&
                    !AttachmentsRetired && !IsDowned && !IsTemporarySummon && ItemId != 0 &&
                    MountSeatAuthorization.SameLivingWorld(author, this) &&
                    author.IsRiding && author.AttachedPoint == AttachPointKind.Driver &&
                    MountSeatAuthorization.IsAttached(author, this) &&
                    Passengers.TryGetValue(AttachPointKind.Driver, out var driver) && driver._objId == author.ObjId &&
                    OwnerId == author.Id && OwnerObjId == author.ObjId &&
                    ParentWorld.MateManager.IsOwnedMate(author, this) && DbInfo != null &&
                    DbInfo.Owner == author.Id && DbInfo.Id == Id && DbInfo.ItemId == ItemId &&
                    ReferenceEquals(author.Mates?.GetMateInfo(ItemId), DbInfo);

                var metres = _ridingMileage.Observe(previousWorldPosition, Transform.World.Position,
                    observedAt, eligible);
                if (metres == 0)
                    return 0;

                var delta = (int)Math.Min(metres, (long)int.MaxValue - Mileage);
                if (delta == 0)
                    return 0;
                var previousMileage = Mileage;
                Mileage += delta;
                DbInfo.Mileage = Mileage;
                author.SendPacket(new SCMileageChangedPacket(ObjId, delta));
                AddExpCore(CalculateRidingExperience(previousMileage, Mileage, AppConfiguration.Instance.World.ExpRate));
                return delta;
            }
        }
    }

    internal static int CalculateRidingExperience(int previousMileage, int currentMileage, double worldRate)
    {
        previousMileage = Math.Max(0, previousMileage);
        if (currentMileage <= previousMileage || !double.IsFinite(worldRate) || worldRate <= 0)
            return 0;
        if (worldRate >= 2d * int.MaxValue)
            return int.MaxValue;

        // The saved total supplies fractional XP credit across stops and resummons.
        // Both endpoints use the current rate, so a rate change grants no catch-up XP.
        var previousExperience = Math.Floor(previousMileage * worldRate / 2);
        var currentExperience = Math.Floor(currentMileage * worldRate / 2);
        return (int)Math.Clamp(currentExperience - previousExperience, 0, int.MaxValue);
    }
}
