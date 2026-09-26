using AAEmu.Game.Models.Game.NPChar;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

public sealed class NpcKillExperienceTests
{
    [Test]
    [Arguments(10, 50)]
    [Arguments(20, 30)]
    [Arguments(30, 20)]
    public async Task KillerFallback_OutsideTheLevelWindowAwardsNoCharacterOrPetExperience(int npcLevel, int killerLevel)
    {
        // No world or reward templates exist. An excluded kill must not reach either award path.
        var npc = new Npc { Level = (byte)npcLevel };
        var killer = new CharacterMock { Level = (byte)killerLevel };
        npc.GrantKillExperience(killer, 1f, 1f);
        await Assert.That(killer.Experience).IsEqualTo(0);
        await Assert.That(killer.Level).IsEqualTo((byte)killerLevel);
    }

    [Test]
    [Arguments(30, 30, 1f, 1000)]
    [Arguments(30, 35, 1f, 500)]
    [Arguments(30, 25, 1f, 1500)]
    [Arguments(30, 39, 1f, 99)]
    [Arguments(30, 21, 1f, 1900)]
    [Arguments(30, 40, 1f, 0)]
    [Arguments(30, 20, 1f, 0)]
    [Arguments(30, 30, 0.33f, 330)]
    [Arguments(30, 30, 0.66f, 660)]
    [Arguments(30, 35, 0.9f, 450)]
    public async Task SharedAward_PreservesTheTaggedLevelAndTeamRules(int npcLevel, int recipientLevel, float share, int expected)
    {
        await Assert.That(Npc.ScaleKillExperience(1000, npcLevel, recipientLevel, share)).IsEqualTo(expected);
    }

    [Test]
    public async Task LargeAward_DoesNotWrapIntoNegativeExperience()
    {
        await Assert.That(Npc.ScaleKillExperience(int.MaxValue, 30, 21, 1f)).IsEqualTo(int.MaxValue);
        await Assert.That(Npc.ScaleKillExperience(0, 30, 30, 1f)).IsEqualTo(0);
    }
}
