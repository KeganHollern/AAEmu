using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public class SkillEffectTransactionTests
{
    [Test]
    public async Task SpawnerInteractionTransaction_SpawnsBeforeAdvancingDoodad()
    {
        var spawnPriority = Skill.GetEffectApplicationPriority(new NpcSpawnerSpawnEffect());
        var interactionPriority = Skill.GetEffectApplicationPriority(new InteractionEffect());

        await Assert.That(spawnPriority).IsLessThan(interactionPriority);
    }

    [Test]
    public async Task SpawnerInteractionTransaction_ConsumesInteractionReagent()
    {
        var interaction = new SkillEffect
        {
            Template = new InteractionEffect(),
            ConsumeItemId = 29301,
            ConsumeItemCount = 1
        };
        var spawn = new SkillEffect { Template = new NpcSpawnerSpawnEffect() };

        var selected = Skill.SelectConsumptionEffect([interaction, spawn], spawn, true);

        await Assert.That(selected).IsSameReferenceAs(interaction);
        await Assert.That(selected.ConsumeItemId).IsEqualTo(29301u);
    }

    [Test]
    public async Task OrdinarySkill_KeepsExistingLastEffectConsumptionRule()
    {
        var first = new SkillEffect
        {
            Template = new InteractionEffect(),
            ConsumeItemId = 29301,
            ConsumeItemCount = 1
        };
        var last = new SkillEffect { Template = new NpcSpawnerSpawnEffect() };

        var selected = Skill.SelectConsumptionEffect([first, last], last, false);

        await Assert.That(selected).IsSameReferenceAs(last);
    }
}
