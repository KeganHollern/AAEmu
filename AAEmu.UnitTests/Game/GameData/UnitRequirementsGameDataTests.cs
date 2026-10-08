using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Spheres;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class UnitRequirementsGameDataTests
{
    private const uint OwnerId = 101;

    [Test]
    [Arguments("QuestComponent", false, false, false, false)]
    [Arguments("QuestComponent", false, false, true, false)]
    [Arguments("QuestComponent", false, true, false, false)]
    [Arguments("QuestComponent", false, true, true, true)]
    [Arguments("QuestComponent", true, false, false, false)]
    [Arguments("QuestComponent", true, false, true, true)]
    [Arguments("QuestComponent", true, true, false, true)]
    [Arguments("QuestComponent", true, true, true, true)]
    [Arguments("Sphere", false, false, false, false)]
    [Arguments("Sphere", false, false, true, false)]
    [Arguments("Sphere", false, true, false, false)]
    [Arguments("Sphere", false, true, true, true)]
    [Arguments("Sphere", true, false, false, false)]
    [Arguments("Sphere", true, false, true, true)]
    [Arguments("Sphere", true, true, false, true)]
    [Arguments("Sphere", true, true, true, true)]
    [Arguments("Skill", false, false, false, false)]
    [Arguments("Skill", false, false, true, false)]
    [Arguments("Skill", false, true, false, false)]
    [Arguments("Skill", false, true, true, true)]
    [Arguments("Skill", true, false, false, false)]
    [Arguments("Skill", true, false, true, true)]
    [Arguments("Skill", true, true, false, true)]
    [Arguments("Skill", true, true, true, true)]
    public async Task Requirements_AndOrRows_UseActualResults(
        string ownerType, bool orUnitReqs, bool firstPasses, bool secondPasses, bool expected)
    {
        var data = CreateData(ownerType,
            new UnitReqs { KindType = UnitReqsKindType.Level, Value1 = firstPasses ? 1u : 30u },
            new UnitReqs { KindType = UnitReqsKindType.Level, Value1 = secondPasses ? 1u : 30u });

        await Assert.That(Evaluate(data, ownerType, orUnitReqs)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("QuestComponent", false)]
    [Arguments("QuestComponent", true)]
    [Arguments("Sphere", false)]
    [Arguments("Sphere", true)]
    [Arguments("Skill", false)]
    [Arguments("Skill", true)]
    public async Task Requirements_NoRows_AllowOwner(string ownerType, bool orUnitReqs)
    {
        var data = CreateData(ownerType);

        await Assert.That(Evaluate(data, ownerType, orUnitReqs)).IsTrue();
    }

    [Test]
    public async Task CanUseSkill_AllOrRequirementsFail_PreservesLastFailureDetails()
    {
        var data = CreateData("Skill",
            new UnitReqs { KindType = UnitReqsKindType.Level, Value1 = 30 },
            new UnitReqs { KindType = UnitReqsKindType.TargetNpc, Value1 = 4242 });

        var result = data.CanUseSkill(new SkillTemplate { Id = OwnerId, OrUnitReqs = true },
            new Unit { Level = 20 }, null);

        await Assert.That(result.ResultKey).IsEqualTo(SkillResultKeys.skill_urk_target_npc);
        await Assert.That(result.ResultUShort).IsEqualTo((ushort)0);
        await Assert.That(result.ResultUInt).IsEqualTo(4242u);
    }

    [Test]
    [Arguments(0, 0, true)]
    [Arguments(9, 0, true)]
    [Arguments(10, 0, false)]
    [Arguments(0, 1, false)]
    [Arguments(9, 6, false)]
    public async Task CanComponentRun_JuryQualification_UsesBothAuthoredPointConditions(
        int infamy, int juryPoints, bool expected)
    {
        // Components 21905 and 21908 require infamy <= 9 and jury points <= 0.
        var data = CreateData("QuestComponent",
            new UnitReqs { KindType = UnitReqsKindType.CrimeRecord, Value1 = 1, Value2 = 9 },
            new UnitReqs { KindType = UnitReqsKindType.JuryPoint, Value1 = 1, Value2 = 0 });
        var player = new CharacterMock { InfamyPoint = infamy, JuryPoint = juryPoints };
        var component = new QuestComponentTemplate(new QuestTemplate()) { Id = OwnerId };

        await Assert.That(data.CanComponentRun(component, player)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 5, false)]
    [Arguments(0, 6, true)]
    [Arguments(9, 6, true)]
    [Arguments(10, 6, false)]
    [Arguments(9, 7, true)]
    public async Task CanComponentRun_ExecutionerQualification_UsesBothAuthoredPointConditions(
        int infamy, int juryPoints, bool expected)
    {
        // Components 22064 and 22070 require infamy <= 9 and jury points >= 6.
        var data = CreateData("QuestComponent",
            new UnitReqs { KindType = UnitReqsKindType.CrimeRecord, Value1 = 1, Value2 = 9 },
            new UnitReqs { KindType = UnitReqsKindType.JuryPoint, Value1 = 0, Value2 = 6 });
        var player = new CharacterMock { InfamyPoint = infamy, JuryPoint = juryPoints };
        var component = new QuestComponentTemplate(new QuestTemplate()) { Id = OwnerId };

        await Assert.That(data.CanComponentRun(component, player)).IsEqualTo(expected);
    }

    [Test]
    public async Task CanUseSkill_LegacyCraftLearningRequirement_MatchesClientSuccess()
    {
        // r208022 explicitly skips kind 19. Craft 169 belongs to legacy skill 10603.
        var data = CreateData("Skill",
            new UnitReqs { KindType = UnitReqsKindType.CanLearnCraft, Value1 = 169 });
        var result = data.CanUseSkill(new SkillTemplate { Id = OwnerId }, new Unit(), null);

        await Assert.That(result.ResultKey).IsEqualTo(SkillResultKeys.ok);
        await Assert.That(result.ResultUShort).IsEqualTo((ushort)0);
        await Assert.That(result.ResultUInt).IsEqualTo(0u);
    }

    private static bool Evaluate(UnitRequirementsGameData data, string ownerType, bool orUnitReqs)
    {
        var unit = new Unit { Level = 20 };
        return ownerType switch
        {
            "QuestComponent" => data.CanComponentRun(
                new QuestComponentTemplate(new QuestTemplate()) { Id = OwnerId, OrUnitReqs = orUnitReqs }, unit),
            "Sphere" => data.CanTriggerSphere(new Spheres { Id = OwnerId, OrUnitReqs = orUnitReqs }, unit),
            "Skill" => data.CanUseSkill(new SkillTemplate { Id = OwnerId, OrUnitReqs = orUnitReqs }, unit, null)
                .ResultKey == SkillResultKeys.ok,
            _ => throw new ArgumentOutOfRangeException(nameof(ownerType))
        };
    }

    private static UnitRequirementsGameData CreateData(string ownerType, params UnitReqs[] requirements)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE unit_reqs (
                                  id INTEGER, owner_id INTEGER, owner_type TEXT,
                                  kind_id INTEGER, value1 INTEGER, value2 INTEGER);
                              """;
        command.ExecuteNonQuery();

        command.CommandText = """
                              INSERT INTO unit_reqs VALUES
                                  ($id, $ownerId, $ownerType, $kindId, $value1, $value2);
                              """;
        for (var index = 0; index < requirements.Length; index++)
        {
            var requirement = requirements[index];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", index + 1);
            command.Parameters.AddWithValue("$ownerId", OwnerId);
            command.Parameters.AddWithValue("$ownerType", ownerType);
            command.Parameters.AddWithValue("$kindId", (uint)requirement.KindType);
            command.Parameters.AddWithValue("$value1", requirement.Value1);
            command.Parameters.AddWithValue("$value2", requirement.Value2);
            command.ExecuteNonQuery();
        }

        var data = new UnitRequirementsGameData();
        data.Load(connection);
        return data;
    }
}
