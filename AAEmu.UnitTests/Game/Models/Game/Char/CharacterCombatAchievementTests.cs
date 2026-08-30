using System.Reflection;

using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed class CharacterCombatAchievementTests
{
    private static readonly FieldInfo s_achievementsField =
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    [Arguments(ZoneConflictType.Conflict)]
    [Arguments(ZoneConflictType.War)]
    public async Task CreateZoneHonorProgressEvent_ConflictAndWarUseZoneSelector(ZoneConflictType zoneState)
    {
        var progressEvent = Character.CreateZoneHonorProgressEvent(22, zoneState, 15);

        await Assert.That(progressEvent.HasValue).IsTrue();
        await Assert.That(progressEvent.Value.Kind).IsEqualTo(CharRecordKind.GetHonorPoint);
        await Assert.That(progressEvent.Value.Value1).IsEqualTo(0u);
        await Assert.That(progressEvent.Value.Value2).IsEqualTo(22u);
        await Assert.That(progressEvent.Value.Amount).IsEqualTo(15u);
    }

    [Test]
    public async Task CreateZoneHonorProgressEvent_PeaceDoesNotCount()
    {
        var progressEvent = Character.CreateZoneHonorProgressEvent(22, ZoneConflictType.Peace, 15);

        await Assert.That(progressEvent.HasValue).IsFalse();
    }

    [Test]
    public async Task RecordPvpDeathAchievements_SameFactionWantedKillCountsBothCharacters()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.DeadByPvp);
        data.AddRecord(101, CharRecordKind.DeadByPvp, 22);
        data.AddRecord(102, CharRecordKind.KillWanted);
        data.AddAchievement(1000, 10, false);
        data.AddObjective(1, 1000, 100);
        data.AddAchievement(1001, 10, false);
        data.AddObjective(2, 1001, 101);
        data.AddAchievement(1002, 10, false);
        data.AddObjective(3, 1002, 102);
        var gameData = data.Build();
        var faction = new SystemFaction { Id = FactionsEnum.Nuian, MotherId = FactionsEnum.NuiaAlliance };
        var victim = new CharacterMock { Id = 7, Faction = faction };
        var killer = new CharacterMock { Id = 8, Faction = faction };
        var victimAchievements = new CharacterAchievements(victim, gameData);
        var killerAchievements = new CharacterAchievements(killer, gameData);
        s_achievementsField.SetValue(victim, victimAchievements);
        s_achievementsField.SetValue(killer, killerAchievements);

        victim.RecordPvpDeathAchievements(killer, 22, true);

        await Assert.That(victimAchievements.GetAmount(1000)).IsEqualTo(1u);
        await Assert.That(victimAchievements.GetAmount(1001)).IsEqualTo(1u);
        await Assert.That(killerAchievements.GetAmount(1002)).IsEqualTo(1u);
    }
}
