using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Placement_ZeroCostHonorsWaiverAndRestoresUnrelatedCancellation(bool succeeds)
    {
        _owner.InitializeLaborCache(0, DateTime.UtcNow);
        _owner.SkillCancelled = true;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => succeeds;
        var result = SkillLaborBatch.RunPlacement(_owner, skill, 0, () =>
            _owner.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, 200, 1));
        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)0);
        await Assert.That(_owner.SkillCancelled).IsTrue();
        await Assert.That(_owner.Inventory.Bag.Items.Any(item => item.TemplateId == 200)).IsEqualTo(succeeds);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(32768)]
    public async Task Placement_InvalidEffectiveCostRejectsBeforeEffects(int cost)
    {
        _owner.SkillCancelled = true;
        var ran = false;
        var result = SkillLaborBatch.RunPlacement(_owner, NewSkill(), cost, () => ran = true);
        await Assert.That(result).IsFalse();
        await Assert.That(ran).IsFalse();
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.SkillCancelled).IsTrue();
    }

    [Test]
    public async Task Placement_UsesRawCostAndNoActabilityReward()
    {
        var skill = NewSkill();
        skill.Template.ActabilityGroupId = 43;
        _owner.Actability = null;
        _owner.SkillCancelled = true;
        var result = SkillLaborBatch.RunPlacement(_owner, skill, 2, () => { });
        await Assert.That(result).IsTrue();
        await Assert.That(_owner.LaborPower).IsEqualTo((short)18);
        await Assert.That(_owner.SkillCancelled).IsTrue();
    }

    [Test]
    public async Task Placement_NestedIndependentRequestRestoresTheOuterSkill()
    {
        var nestedRan = false;
        var result = SkillLaborBatch.Run(_owner, NewSkill(), true, () =>
        {
            _owner.Inventory.Bag.AcquireDefaultItem(ItemTaskType.SkillEffectGainItem, 200, 1);
            SkillLaborBatch.RunPlacement(_owner, NewSkill(), 0, () => nestedRan = true);
        });
        await Assert.That(result).IsFalse();
        await Assert.That(nestedRan).IsFalse();
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(_material);
    }
}
