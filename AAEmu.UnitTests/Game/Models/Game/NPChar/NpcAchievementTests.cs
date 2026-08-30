using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.UnitTests.Utils.Mocks;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;
using GameTeam = AAEmu.Game.Models.Game.Team.Team;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public sealed class NpcAchievementTests
{
    private static readonly FieldInfo s_achievementGameDataInstanceField =
        typeof(Singleton<AchievementGameData>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo s_recordKillAchievementsMethod =
        typeof(Npc).GetMethod("RecordKillAchievements", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public async Task RecordKillAchievements_PartyMembersSelectMatchingLevelCapOnly()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 9000);
        data.AddRecord(101, CharRecordKind.KillNpc, 9000, 29);
        data.AddRecord(102, CharRecordKind.KillNpc, 9000, 30);
        data.AddRecord(103, CharRecordKind.KillNpc, 9001, 30);
        for (uint index = 0; index < 4; index++)
        {
            data.AddAchievement(1000 + index, 10, false);
            data.AddObjective(1 + index, 1000 + index, 100 + index);
        }

        var previousGameData = s_achievementGameDataInstanceField.GetValue(null);
        s_achievementGameDataInstanceField.SetValue(null, data.Build());
        try
        {
            var recipient = new CharacterMock { Level = 20 };
            var teammate = new CharacterMock { Level = 30 };
            HashSet<Character> eligiblePlayers = [recipient, teammate];
            var taggedTeam = new GameTeam { IsParty = true };
            var npc = new Npc { TemplateId = 9000 };

            s_recordKillAchievementsMethod.Invoke(npc, [recipient, eligiblePlayers, taggedTeam]);

            await Assert.That(recipient.Achievements.GetAmount(1000)).IsEqualTo(1u);
            await Assert.That(recipient.Achievements.GetAmount(1001)).IsEqualTo(0u);
            await Assert.That(recipient.Achievements.GetAmount(1002)).IsEqualTo(1u);
            await Assert.That(recipient.Achievements.GetAmount(1003)).IsEqualTo(0u);
        }
        finally
        {
            s_achievementGameDataInstanceField.SetValue(null, previousGameData);
        }
    }
}
