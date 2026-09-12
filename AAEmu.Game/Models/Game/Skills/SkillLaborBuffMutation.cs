using System.Runtime.CompilerServices;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using UnitBuffs = AAEmu.Game.Models.Game.Units.Buffs;

namespace AAEmu.Game.Models.Game.Skills;

/// <summary>Persists a detached result before any live buff callback runs.</summary>
internal sealed class SkillLaborBuffMutation
{
    private static readonly ConditionalWeakTable<SkillLaborBatch, Dictionary<UnitBuffs, SkillLaborBuffMutation>> s_batches = new();
    private readonly UnitBuffs _preview;

    private SkillLaborBuffMutation(SkillLaborBatch batch, UnitBuffs buffs, Character target)
    {
        _preview = buffs.CreateLaborPreview();
        batch.EnlistCharacter(target);
        batch.Enlist(context => _preview.SaveLaborBuffs(context, target.Id), null);
    }

    private static UnitBuffs Preview(SkillLaborBatch batch, UnitBuffs buffs, Character target)
    {
        var plans = s_batches.GetOrCreateValue(batch);
        if (!plans.TryGetValue(buffs, out var plan))
        {
            plan = new SkillLaborBuffMutation(batch, buffs, target);
            plans.Add(buffs, plan);
        }
        return plan._preview;
    }

    internal static IBuffs Read(IBuffs buffs)
    {
        if (SkillLaborBatch.Current is { } batch && buffs is UnitBuffs real &&
            s_batches.TryGetValue(batch, out var plans) && plans.TryGetValue(real, out var plan))
            return plan._preview;
        return buffs;
    }

    internal static void Stage(SkillLaborBatch batch, UnitBuffs buffs, Buff buff, uint index, int forcedDuration)
    {
        // Start chooses the initial charge. Choose it once so the persisted value and live value agree.
        if (buff.Charge == 0)
            buff.Charge = Random.Shared.Next(buff.Template.InitMinCharge, buff.Template.InitMaxCharge);
        if (buff.Owner is Character target)
            Preview(batch, buffs, target).AddBuff(UnitBuffs.CloneLaborBuff(buff), index, forcedDuration);
        batch.AfterCommit(() => buffs.AddBuff(buff, index, forcedDuration));
    }

    internal static void StageRemoval(SkillLaborBatch batch, UnitBuffs buffs, BaseUnit owner,
        BuffKind kind, int count, uint buffTagId)
    {
        if (owner is Character target)
            Preview(batch, buffs, target).RemoveBuffs(kind, count, buffTagId);
        batch.AfterCommit(() => buffs.RemoveBuffs(kind, count, buffTagId));
    }
}
