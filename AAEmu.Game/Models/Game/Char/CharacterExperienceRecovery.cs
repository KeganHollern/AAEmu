using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    internal bool StageExperienceRecovery(short laborCost, bool restoreFullLoss)
    {
        var batch = SkillLaborBatch.For(this);
        if (batch == null)
            return false;
        // Older saves retain only the priest share. Keep that known amount when the full loss is unknown.
        var useFullLoss = restoreFullLoss && LastExpLoss > 0;
        var amount = useFullLoss ? LastExpLoss : RecoverableExp;
        if (amount <= 0)
        {
            SendErrorMessage(ErrorMessageType.CannotRecoverAllNotEnough);
            return batch.Fail();
        }
        if (laborCost < 0 || LaborPower < laborCost)
        {
            SendErrorMessage(ErrorMessageType.NotEnoughLaborPower);
            return batch.Fail();
        }

        var laborBefore = LaborPower;
        var consumedBefore = ConsumedLaborPower;
        var experienceBefore = Experience;
        var levelBefore = Level;
        var recoverableBefore = RecoverableExp;
        var lostBefore = LastExpLoss;
        batch.Enlist(context =>
        {
            if (laborCost == 0)
                return;
            using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = "UPDATE accounts SET labor=@after WHERE account_id=@id AND labor=@before";
            command.Parameters.AddWithValue("@id", AccountId);
            command.Parameters.AddWithValue("@before", laborBefore);
            command.Parameters.AddWithValue("@after", LaborPower);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The account labor changed before experience recovery.");
        }, () =>
        {
            RestoreStagedLabor(laborBefore, consumedBefore);
            Experience = experienceBefore;
            Level = levelBefore;
            RecoverableExp = recoverableBefore;
            LastExpLoss = lostBefore;
        });
        if (laborCost > 0 && !TryStageLaborConsumption(laborCost))
            return batch.Fail();

        // Recovery restores the lost amount. It grants no labor or active ability XP.
        var publishExperience = PrepareExperienceReward(amount, false);
        RecoverableExp = 0;
        LastExpLoss = 0;
        batch.AfterCommit(() =>
        {
            if (laborCost > 0)
            {
                Achievements?.Increment(CharRecordKind.SpendLabor, 0, 0, (uint)laborCost);
                SendPacket(new SCCharacterLaborPowerChangedPacket(-laborCost, 0, 0, 0));
            }
            // r208022 reports the previous full loss for reason 2 and the previous priest share for reason 1.
            SendPacket(new SCRecoverableExpPacket(ObjId, 0, 0, useFullLoss ? 2 : 1));
            publishExperience();
        });
        return true;
    }
}
