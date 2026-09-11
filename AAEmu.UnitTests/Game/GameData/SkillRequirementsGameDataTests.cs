using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class SkillRequirementsGameDataTests
{
    [Test]
    [Arguments(false, false, false, 0u)]
    [Arguments(false, true, false, 0u)]
    [Arguments(true, false, false, 0u)]
    [Arguments(true, true, false, 0u)]
    [Arguments(false, false, true, 1u)]
    [Arguments(false, true, true, 0u)]
    [Arguments(true, false, true, 0u)]
    [Arguments(true, true, true, 1u)]
    public async Task DefaultResult_IsTheResultOutsideTheExceptionList(bool defaultResult, bool listed, bool hasBuff, uint expected)
    {
        using var connection = CreateConnection();
        Execute(connection, $"INSERT INTO skill_reqs VALUES(1, 'f', 2149, 0, '{(defaultResult ? "t" : "f")}'); INSERT INTO skill_req_skills VALUES(1, 100);");
        var data = Load(connection);
        var caster = Subject(buffId: hasBuff ? 2149u : 0);
        await Assert.That(data.GetFailedRequirement(listed ? 100u : 101u, SkillTargetType.Self, [], caster, caster)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(11020u, 27u, 1u)]
    [Arguments(12373u, 306u, 13u)]
    [Arguments(15660u, 367u, 35u)]
    public async Task AuthoredRootAndBackpackDenylists_BlockBoundSkills(uint skillId, uint buffTag, uint requirementId)
    {
        using var connection = CreateConnection();
        Execute(connection, $"INSERT INTO skill_reqs VALUES({requirementId}, 'f', 0, {buffTag}, 't'); INSERT INTO skill_req_skills VALUES({requirementId}, {skillId});");
        var data = Load(connection);
        var caster = Subject(buffTag: buffTag);
        await Assert.That(data.GetFailedRequirement(skillId, SkillTargetType.Self, [], caster, caster)).IsEqualTo(requirementId);
        await Assert.That(data.GetFailedRequirement(skillId + 1, SkillTargetType.Self, [], caster, caster)).IsEqualTo(0u);
    }

    [Test]
    public async Task NuiAllowlist_AllowsPeacefulTaggedSkillAndRejectsAttack()
    {
        using var connection = CreateConnection();
        Execute(connection, "INSERT INTO skill_reqs VALUES(25, 'f', 2149, 0, 'f'); INSERT INTO skill_req_skill_tags VALUES(25, 402);");
        var data = Load(connection);
        var caster = Subject(buffId: 2149);
        await Assert.That(data.GetFailedRequirement(100, SkillTargetType.Self, [402], caster, caster)).IsEqualTo(0u);
        await Assert.That(data.GetFailedRequirement(2, SkillTargetType.Hostile, [], caster, new Unit())).IsEqualTo(25u);
        await Assert.That(data.GetFailedRequirement(2, SkillTargetType.Hostile, [], new Unit(), caster)).IsEqualTo(0u);
    }

    [Test]
    public async Task TargetRequirement_UsesResolvedTargetAndNotCastersCurrentTarget()
    {
        using var connection = CreateConnection();
        Execute(connection, "INSERT INTO skill_reqs VALUES(50, 't', 742, 0, 't'); INSERT INTO skill_req_skill_tags VALUES(50, 358);");
        var data = Load(connection);
        var protectedTarget = Subject(buffId: 742);
        var clearTarget = new Unit();
        var caster = new Unit { CurrentTarget = protectedTarget };
        await Assert.That(data.GetFailedRequirement(100, SkillTargetType.AnyUnit, [358], caster, clearTarget)).IsEqualTo(0u);
        caster.CurrentTarget = clearTarget;
        await Assert.That(data.GetFailedRequirement(100, SkillTargetType.AnyUnit, [358], caster, protectedTarget)).IsEqualTo(50u);
        await Assert.That(data.GetFailedRequirement(100, SkillTargetType.AnyUnit, [358], protectedTarget, clearTarget)).IsEqualTo(0u);
    }

    [Test]
    [Arguments(SkillTargetType.Self, true)]
    [Arguments(SkillTargetType.Friendly, true)]
    [Arguments(SkillTargetType.Party, true)]
    [Arguments(SkillTargetType.Raid, true)]
    [Arguments(SkillTargetType.Hostile, true)]
    [Arguments(SkillTargetType.AnyUnit, true)]
    [Arguments(SkillTargetType.Pos, false)]
    [Arguments(SkillTargetType.Line, false)]
    [Arguments(SkillTargetType.Doodad, false)]
    [Arguments(SkillTargetType.Item, false)]
    [Arguments(SkillTargetType.Pet, true)]
    [Arguments(SkillTargetType.BallisticPos, false)]
    [Arguments(SkillTargetType.SummonPos, false)]
    [Arguments(SkillTargetType.RelativePos, false)]
    [Arguments(SkillTargetType.SourcePos, false)]
    [Arguments(SkillTargetType.ArtilleryPos, false)]
    [Arguments(SkillTargetType.Others, true)]
    [Arguments(SkillTargetType.FriendlyOthers, true)]
    [Arguments(SkillTargetType.CursorPos, false)]
    [Arguments(SkillTargetType.Building, true)]
    public async Task TargetTypes_MatchR208022NativeFilter(SkillTargetType targetType, bool checksBuff)
    {
        using var connection = CreateConnection();
        Execute(connection, "INSERT INTO skill_reqs VALUES(1, 't', 2149, 0, 't'); INSERT INTO skill_req_skills VALUES(1, 100);");
        var data = Load(connection);
        await Assert.That(data.GetFailedRequirement(100, targetType, [], new Unit(), Subject(buffId: 2149)))
            .IsEqualTo(checksBuff ? 1u : 0u);
    }

    [Test]
    public async Task Relations_AreUnionAndFirstAuthoredRuleWins()
    {
        using var connection = CreateConnection();
        Execute(connection, """
            INSERT INTO skill_reqs VALUES(35, 'f', 0, 367, 't'), (13, 'f', 0, 306, 't');
            INSERT INTO skill_req_skills VALUES(35, 15660), (13, 15660), (13, 15660);
            INSERT INTO skill_req_skill_tags VALUES(13, 296), (13, 296);
            """);
        var data = Load(connection);
        var buffs = Mock.Of<IBuffs>();
        buffs.CheckBuffTag(306).Returns(true);
        buffs.CheckBuffTag(367).Returns(true);
        var caster = new Unit { Buffs = buffs.Object };
        await Assert.That(data.GetFailedRequirement(15660, SkillTargetType.Self, [], caster, caster)).IsEqualTo(13u);
        await Assert.That(data.GetFailedRequirement(12373, SkillTargetType.Self, [296], caster, caster)).IsEqualTo(13u);
    }

    [Test]
    public async Task Reload_DropsOldRules()
    {
        using var connection = CreateConnection();
        Execute(connection, "INSERT INTO skill_reqs VALUES(1, 'f', 2149, 0, 'f');");
        var data = Load(connection);
        var caster = Subject(buffId: 2149);
        await Assert.That(data.GetFailedRequirement(2, SkillTargetType.Self, [], caster, caster)).IsEqualTo(1u);
        Execute(connection, "DELETE FROM skill_reqs;");
        data.Load(connection);
        await Assert.That(data.GetFailedRequirement(2, SkillTargetType.Self, [], caster, caster)).IsEqualTo(0u);
    }

    [Test]
    public void MissingRelationOwner_StopsLoad()
    {
        using var connection = CreateConnection();
        Execute(connection, "INSERT INTO skill_req_skills VALUES(999, 100);");
        Assert.Throws<InvalidDataException>(() => Load(connection));
    }

    private static Unit Subject(uint buffId = 0, uint buffTag = 0)
    {
        var buffs = Mock.Of<IBuffs>();
        if (buffId != 0) buffs.CheckBuff(buffId).Returns(true);
        if (buffTag != 0) buffs.CheckBuffTag(buffTag).Returns(true);
        return new Unit { Buffs = buffs.Object };
    }

    internal static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE skill_reqs(id INTEGER, target TEXT, buff_id INTEGER, buff_tag_id INTEGER, default_result TEXT);
            CREATE TABLE skill_req_skills(skill_req_id INTEGER, skill_id INTEGER);
            CREATE TABLE skill_req_skill_tags(skill_req_id INTEGER, skill_tag_id INTEGER);
            """);
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static SkillRequirementsGameData Load(SqliteConnection connection)
    {
        var data = new SkillRequirementsGameData();
        data.Load(connection);
        return data;
    }
}
