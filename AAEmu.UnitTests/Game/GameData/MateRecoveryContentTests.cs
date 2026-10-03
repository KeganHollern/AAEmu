using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class MateRecoveryContentTests
{
    [Test]
    [Arguments(13719, 6851, 8380, 2215, SpecialType.MateMakeGetUp, 0, 0)]
    [Arguments(15220, 10130, 12554, 3461, SpecialType.HealPet, 20, 20)]
    public async Task ExactClient_RecoverySkillsReachTheAuthoredSpecialEffects(
        int skillId, int skillEffectId, int effectId, int specialEffectId,
        SpecialType type, int health, int mana)
    {
        using var connection = OpenCompact();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT se.id, e.id, special.id, special.special_effect_type_id,
                   special.value1, special.value2, special.value3, special.value4,
                   se.application_method_id, se.chance
            FROM skill_effects se
            JOIN effects e ON e.id = se.effect_id AND e.actual_type = 'SpecialEffect'
            JOIN special_effects special ON special.id = e.actual_id
            WHERE se.skill_id = $skill
            """;
        command.Parameters.AddWithValue("$skill", skillId);
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt32(0)).IsEqualTo(skillEffectId);
        await Assert.That(reader.GetInt32(1)).IsEqualTo(effectId);
        await Assert.That(reader.GetInt32(2)).IsEqualTo(specialEffectId);
        await Assert.That(reader.GetInt32(3)).IsEqualTo((int)type);
        await Assert.That(reader.GetInt32(4)).IsEqualTo(health);
        await Assert.That(reader.GetInt32(5)).IsEqualTo(mana);
        await Assert.That(reader.GetInt32(6)).IsEqualTo(0);
        await Assert.That(reader.GetInt32(7)).IsEqualTo(0);
        await Assert.That(reader.GetInt32(8)).IsEqualTo(1);
        await Assert.That(reader.GetInt32(9)).IsEqualTo(100);
        await Assert.That(reader.Read()).IsFalse();
    }

    [Test]
    [Arguments(13719, 5000, 0, 4, SkillTargetType.Self, SkillTargetRelation.Friendly)]
    [Arguments(15220, 2000, 5000, 3, SkillTargetType.Friendly, SkillTargetRelation.Any)]
    public async Task ExactClient_RecoverySkillsUseLivingTargetsAndAuthoredTiming(
        int skillId, int castMilliseconds, int cooldownMilliseconds, int rangeMetres,
        SkillTargetType targetType, SkillTargetRelation relation)
    {
        using var connection = OpenCompact();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT casting_time, cooldown_time, min_range, max_range,
                   target_type_id, target_relation_id, target_selection_id,
                   source_alive, source_dead, target_alive, target_dead
            FROM skills WHERE id = $skill
            """;
        command.Parameters.AddWithValue("$skill", skillId);
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt32(0)).IsEqualTo(castMilliseconds);
        await Assert.That(reader.GetInt32(1)).IsEqualTo(cooldownMilliseconds);
        await Assert.That(reader.GetInt32(2)).IsEqualTo(0);
        await Assert.That(reader.GetInt32(3)).IsEqualTo(rangeMetres);
        await Assert.That(reader.GetInt32(4)).IsEqualTo((int)targetType);
        await Assert.That(reader.GetInt32(5)).IsEqualTo((int)relation);
        await Assert.That(reader.GetInt32(6)).IsEqualTo(2);
        await Assert.That(reader.GetString(7)).IsEqualTo("t");
        await Assert.That(reader.GetString(8)).IsEqualTo("f");
        await Assert.That(reader.GetString(9)).IsEqualTo("t");
        await Assert.That(reader.GetString(10)).IsEqualTo("f");
    }

    [Test]
    public async Task ExactClient_RecoveryPotionUsesItsSkillAndConsumesTheSourceItem()
    {
        using var connection = OpenCompact();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT use_skill_id, use_skill_as_reagent FROM items WHERE id = 18649";
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt32(0)).IsEqualTo(15220);
        await Assert.That(reader.GetString(1)).IsEqualTo("t");
    }

    [Test]
    [Arguments(1578, false)]
    [Arguments(1579, true)]
    public async Task ExactClient_InjuryAndDownedBuffsHaveNoExpiryAndUseLivingUnits(int buffId, bool downed)
    {
        using var connection = OpenCompact();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT duration, tick, system, remove_on_death, dead_applicable, save_rule_id,
                   stun, ragdoll, pacifist, melee_immune, ranged_immune, spell_immune,
                   siege_immune, knockback_immune, fall_damage_immune
            FROM buffs WHERE id = $buff
            """;
        command.Parameters.AddWithValue("$buff", buffId);
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt32(0)).IsEqualTo(0);
        await Assert.That(reader.GetInt32(1)).IsEqualTo(0);
        await Assert.That(reader.GetString(2)).IsEqualTo("t");
        await Assert.That(reader.GetString(3)).IsEqualTo("t");
        await Assert.That(reader.GetString(4)).IsEqualTo("f");
        await Assert.That(reader.GetInt32(5)).IsEqualTo(1);
        for (var column = 6; column <= 14; column++)
            await Assert.That(reader.GetString(column)).IsEqualTo(downed ? "t" : "f");
    }

    [Test]
    public async Task ExactClient_InjuryHasTheThirtyPercentSlowAndNoAuthoredHealthModifier()
    {
        using var connection = OpenCompact();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT owner_id, unit_attribute_id, unit_modifier_type_id, value, linear_level_bonus
                FROM unit_modifiers WHERE owner_type = 'Buff' AND owner_id IN (1578,1579)
                """;
            using var reader = command.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(reader.GetInt32(0)).IsEqualTo(1578);
            await Assert.That(reader.GetInt32(1)).IsEqualTo((int)UnitAttribute.MoveSpeedMul);
            await Assert.That(reader.GetInt32(2)).IsEqualTo((int)UnitModifierType.Value);
            await Assert.That(reader.GetInt32(3)).IsEqualTo(-300);
            await Assert.That(reader.GetInt32(4)).IsEqualTo(0);
            await Assert.That(reader.Read()).IsFalse();
        }
        using var dynamicCommand = connection.CreateCommand();
        dynamicCommand.CommandText = "SELECT count(*) FROM dynamic_unit_modifiers WHERE buff_id IN (1578,1579)";
        await Assert.That(Convert.ToInt32(dynamicCommand.ExecuteScalar())).IsEqualTo(0);
    }

    [Test]
    public async Task ExactClient_DownedRequirementAllowsThePotionAndRejectsOrdinaryHealing()
    {
        using var connection = OpenCompact();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT tagged.buff_id, req.target, req.default_result, relation.skill_id
                FROM skill_reqs req
                JOIN tagged_buffs tagged ON tagged.tag_id = req.buff_tag_id
                JOIN skill_req_skills relation ON relation.skill_req_id = req.id
                WHERE req.id = 37 AND req.buff_tag_id = 371
                """;
            using var reader = command.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(reader.GetInt32(0)).IsEqualTo(1579);
            await Assert.That(reader.GetString(1)).IsEqualTo("t");
            await Assert.That(reader.GetString(2)).IsEqualTo("f");
            await Assert.That(reader.GetInt32(3)).IsEqualTo(15220);
            await Assert.That(reader.Read()).IsFalse();
        }
        var requirements = new SkillRequirementsGameData();
        requirements.Load(connection);
        var caster = new Unit { Buffs = Mock.Of<IBuffs>().Object };
        var downedBuffs = Mock.Of<IBuffs>();
        downedBuffs.CheckBuffTag(371).Returns(true);
        var downed = new Unit { Buffs = downedBuffs.Object };
        await Assert.That(requirements.GetFailedRequirement(15220, SkillTargetType.Friendly, [402], caster, downed))
            .IsEqualTo(0u);
        await Assert.That(requirements.GetFailedRequirement(100, SkillTargetType.Friendly, [], caster, downed))
            .IsEqualTo(37u);
        await Assert.That(requirements.GetFailedRequirement(100, SkillTargetType.Friendly, [], caster,
                new Unit { Buffs = Mock.Of<IBuffs>().Object }))
            .IsEqualTo(0u);
    }

    [Test]
    [Arguments(2149u, 25u)]
    [Arguments(2221u, 34u)]
    [Arguments(4828u, 115u)]
    public async Task ExactClient_GetUpPassesTheAuthoredPeaceZoneRequirements(uint buffId, uint requirementId)
    {
        using var connection = OpenCompact();
        var requirements = new SkillRequirementsGameData();
        requirements.Load(connection);
        var buffs = Mock.Of<IBuffs>();
        buffs.CheckBuff(buffId).Returns(true);
        var caster = new Unit { Buffs = buffs.Object };
        await Assert.That(requirements.GetFailedRequirement(13719, SkillTargetType.Self, [356], caster, caster))
            .IsEqualTo(0u);
        await Assert.That(requirements.GetFailedRequirement(2, SkillTargetType.Hostile, [], caster,
                new Unit { Buffs = Mock.Of<IBuffs>().Object }))
            .IsEqualTo(requirementId);
    }

    private static SqliteConnection OpenCompact()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = compact, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        return connection;
    }
}
