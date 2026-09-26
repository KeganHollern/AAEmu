using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task InteractionCredit_UsesTheCommittedEffectSnapshotOnly(bool commitSucceeds)
    {
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => commitSucceeds;
        var target = new Doodad { TemplateId = 2590 };
        SetField(target, "_funcGroupId", 5522u);
        var received = new List<OnInteractionArgs>();
        _owner.Events.OnInteraction += (_, args) => received.Add(args);
        var deliveredBeforeCommit = false;

        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            InteractionEffect.PublishQuestInteraction(_owner, target, (WorldInteractionType)19, skill);
            deliveredBeforeCommit = received.Count != 0;
            // A later effect changes the same object before the deferred notification runs.
            target.TemplateId = 3000;
            SetField(target, "_funcGroupId", 5523u);
        });

        await Assert.That(deliveredBeforeCommit).IsFalse();
        await Assert.That(result).IsEqualTo(commitSucceeds);
        await Assert.That(received.Count).IsEqualTo(commitSucceeds ? 1 : 0);
        if (commitSucceeds)
        {
            await Assert.That(received[0].DoodadId).IsEqualTo(2590u);
            await Assert.That(received[0].WorldInteractionId).IsEqualTo((WorldInteractionType)19);
            await Assert.That(received[0].Phase).IsEqualTo(5522u);
            await Assert.That(received[0].SourcePlayer).IsSameReferenceAs(_owner);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task InteractionCredit_CancelledCharacterOrSkillPublishesNothing(bool cancelCharacter)
    {
        var skill = NewSkill();
        var target = new Doodad { TemplateId = 2590 };
        var deliveries = 0;
        _owner.Events.OnInteraction += (_, _) => deliveries++;
        _owner.SkillCancelled = cancelCharacter;
        skill.Cancelled = !cancelCharacter;
        InteractionEffect.PublishQuestInteraction(_owner, target, (WorldInteractionType)19, skill);
        await Assert.That(deliveries).IsEqualTo(0);
    }
}
