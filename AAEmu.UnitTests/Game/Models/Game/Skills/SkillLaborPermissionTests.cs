using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    public async Task DoodadPermissionDenied_RestoresLaborAndEarlierRewards()
    {
        var skill = NewSkill();
        var commits = 0;
        skill.CommitLaborBatch = (_, _) => { commits++; return true; };
        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            _owner.Inventory.Bag.AcquireDefaultItem(ItemTaskType.SkillEffectGainItem, 200, 1);
            DoodadPermissionRules.Demand(_owner, new Doodad { OwnerId = 99 }, 1);
        });
        await Assert.That(result).IsFalse();
        await Assert.That(commits).IsEqualTo(0);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(_material);
    }

    [Test]
    public async Task RecoverItem_KnownCommitFailure_RestoresItemAndDoodadReference()
    {
        _owner.Inventory.Bag.Items.Clear();
        _owner.Inventory.Bag.UpdateFreeSlotCount();
        _owner.Inventory.SystemContainer.Items.Add(_material);
        _material._holdingContainer = _owner.Inventory.SystemContainer;
        _material.SlotType = SlotType.System;
        var doodad = new Doodad { OwnerId = _owner.Id, ItemId = _material.Id, ItemTemplateId = _material.TemplateId };
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => false;
        var recovered = false;

        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
            recovered = new DoodadFuncRecoverItem().TryRecover(_owner, doodad));

        await Assert.That(recovered).IsTrue();
        await Assert.That(result).IsFalse();
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Inventory.SystemContainer.Items.Single()).IsSameReferenceAs(_material);
        await Assert.That(_material._holdingContainer).IsSameReferenceAs(_owner.Inventory.SystemContainer);
        await Assert.That(_owner.Inventory.Bag.Items).IsEmpty();
        await Assert.That(doodad.ItemId).IsEqualTo(_material.Id);
        await Assert.That(doodad.ItemTemplateId).IsEqualTo(_material.TemplateId);
    }
}
