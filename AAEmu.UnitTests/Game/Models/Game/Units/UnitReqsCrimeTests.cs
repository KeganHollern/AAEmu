using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed class UnitReqsCrimeTests
{
    [Test]
    [Arguments(UnitReqsKindType.CrimePoint, 0u, 8, false)]
    [Arguments(UnitReqsKindType.CrimePoint, 0u, 9, true)]
    [Arguments(UnitReqsKindType.CrimePoint, 0u, 10, true)]
    [Arguments(UnitReqsKindType.CrimePoint, 1u, 8, true)]
    [Arguments(UnitReqsKindType.CrimePoint, 1u, 9, true)]
    [Arguments(UnitReqsKindType.CrimePoint, 1u, 10, false)]
    [Arguments(UnitReqsKindType.CrimeRecord, 0u, 8, false)]
    [Arguments(UnitReqsKindType.CrimeRecord, 0u, 9, true)]
    [Arguments(UnitReqsKindType.CrimeRecord, 0u, 10, true)]
    [Arguments(UnitReqsKindType.CrimeRecord, 1u, 8, true)]
    [Arguments(UnitReqsKindType.CrimeRecord, 1u, 9, true)]
    [Arguments(UnitReqsKindType.CrimeRecord, 1u, 10, false)]
    [Arguments(UnitReqsKindType.JuryPoint, 0u, 8, false)]
    [Arguments(UnitReqsKindType.JuryPoint, 0u, 9, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 0u, 10, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 1u, 8, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 1u, 9, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 1u, 10, false)]
    public async Task Validate_PointThreshold_IncludesEqualityAndUsesComparisonDirection(
        UnitReqsKindType kind, uint comparison, int points, bool expected)
    {
        var requirement = new UnitReqs { KindType = kind, Value1 = comparison, Value2 = 9 };
        var owner = Player(kind, points);

        var result = requirement.Validate(owner, null);

        await AssertResult(result, expected, kind, comparison);
    }

    [Test]
    [Arguments(UnitReqsKindType.CrimePoint, 8, true)]
    [Arguments(UnitReqsKindType.CrimePoint, 10, false)]
    [Arguments(UnitReqsKindType.CrimeRecord, 8, true)]
    [Arguments(UnitReqsKindType.CrimeRecord, 10, false)]
    [Arguments(UnitReqsKindType.JuryPoint, 8, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 10, false)]
    public async Task Validate_NonzeroComparison_UsesUpperBound(UnitReqsKindType kind, int points, bool expected)
    {
        var requirement = new UnitReqs { KindType = kind, Value1 = 7, Value2 = 9 };

        await AssertResult(requirement.Validate(Player(kind, points), null), expected, kind, 7);
    }

    [Test]
    [Arguments(UnitReqsKindType.CrimeRecord, 0u, 1u, 0, false)]
    [Arguments(UnitReqsKindType.CrimeRecord, 0u, 1u, 1, true)]
    [Arguments(UnitReqsKindType.CrimeRecord, 0u, 1u, 2, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 1u, 0u, 0, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 1u, 0u, 1, false)]
    [Arguments(UnitReqsKindType.JuryPoint, 0u, 6u, 5, false)]
    [Arguments(UnitReqsKindType.JuryPoint, 0u, 6u, 6, true)]
    [Arguments(UnitReqsKindType.JuryPoint, 0u, 6u, 7, true)]
    public async Task Validate_PrisonAndJuryQuestRows_UseAuthoredThresholds(
        UnitReqsKindType kind, uint comparison, uint threshold, int points, bool expected)
    {
        var requirement = new UnitReqs { KindType = kind, Value1 = comparison, Value2 = threshold };

        await AssertResult(requirement.Validate(Player(kind, points), null), expected, kind, comparison);
    }

    [Test]
    [Arguments(UnitReqsKindType.CrimePoint)]
    [Arguments(UnitReqsKindType.CrimeRecord)]
    [Arguments(UnitReqsKindType.JuryPoint)]
    public async Task Validate_PointRequirement_ReadsMatchingOwnerFieldOnly(UnitReqsKindType kind)
    {
        var owner = new Character(null) { CrimePoint = 20, InfamyPoint = 20, JuryPoint = 20 };
        SetPoints(owner, kind, 8);
        var target = Player(kind, 10);
        var requirement = new UnitReqs { KindType = kind, Value1 = 0, Value2 = 9 };

        await AssertResult(requirement.Validate(owner, target), false, kind, 0);
        await AssertResult(requirement.Validate(target, owner), true, kind, 0);
    }

    [Test]
    [Arguments(UnitReqsKindType.CrimePoint)]
    [Arguments(UnitReqsKindType.CrimeRecord)]
    [Arguments(UnitReqsKindType.JuryPoint)]
    [Arguments(UnitReqsKindType.VerdictOnly)]
    public async Task Validate_MissingOrNonPlayerOwner_FailsEvenWithQualifiedTarget(UnitReqsKindType kind)
    {
        var requirement = new UnitReqs { KindType = kind, Value1 = 1, Value2 = 9 };
        var qualifiedTarget = new Character(null) { CrimePoint = 9, InfamyPoint = 9, JuryPoint = 9 };
        BaseUnit[] owners = [null, new BaseUnit(), new Unit(), new Npc()];

        foreach (var owner in owners)
            await AssertResult(requirement.Validate(owner, qualifiedTarget), false, kind,
                kind == UnitReqsKindType.VerdictOnly ? 0u : 1u);
    }

    [Test]
    [Arguments(-1, true)]
    [Arguments(0, false)]
    [Arguments(1, true)]
    [Arguments(6, true)]
    public async Task Validate_VerdictOnly_NeedsOwnersJuryPrivilege(int juryPoints, bool expected)
    {
        var requirement = new UnitReqs { KindType = UnitReqsKindType.VerdictOnly, Value1 = 123, Value2 = 456 };
        var owner = Player(UnitReqsKindType.JuryPoint, juryPoints);
        var target = Player(UnitReqsKindType.JuryPoint, juryPoints == 0 ? 1 : 0);

        await AssertResult(requirement.Validate(owner, target), expected, UnitReqsKindType.VerdictOnly, 0);
    }

    private static Character Player(UnitReqsKindType kind, int points)
    {
        var player = new Character(null);
        SetPoints(player, kind, points);
        return player;
    }

    private static void SetPoints(Character player, UnitReqsKindType kind, int points)
    {
        switch (kind)
        {
            case UnitReqsKindType.CrimePoint:
                player.CrimePoint = (short)points;
                break;
            case UnitReqsKindType.CrimeRecord:
                player.InfamyPoint = points;
                break;
            case UnitReqsKindType.JuryPoint:
                player.JuryPoint = points;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static async Task AssertResult(UnitReqsValidationResult result, bool expected,
        UnitReqsKindType kind, uint comparison)
    {
        var failureCode = kind switch
        {
            UnitReqsKindType.CrimePoint => 103,
            UnitReqsKindType.CrimeRecord => 106,
            UnitReqsKindType.JuryPoint => 107,
            UnitReqsKindType.VerdictOnly => 113,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await Assert.That(result.ResultKey == SkillResultKeys.ok).IsEqualTo(expected);
        if (!expected)
            await Assert.That((int)SkillResultHelper.SkillResultErrorKeyToId(result.ResultKey)).IsEqualTo(failureCode);
        await Assert.That(result.ResultUShort).IsEqualTo((ushort)0);
        await Assert.That(result.ResultUInt).IsEqualTo(expected ? 0u : comparison);
    }
}
