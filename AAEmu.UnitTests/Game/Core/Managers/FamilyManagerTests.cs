using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.UnitTests.Utils.Mocks;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class FamilyManagerTests
{
    private static readonly FieldInfo s_familiesField =
        typeof(FamilyManager).GetField("_families", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_familyMembersField =
        typeof(FamilyManager).GetField("_familyMembers", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_achievementsField =
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockWorld = Mock.Of<IWorldManager>();
        var mockChat = Mock.Of<IChatManager>();
        var mockFamilyId = Mock.Of<IFamilyIdManager>();
        var manager = new FamilyManager(mockWorld.Object, mockChat.Object, mockFamilyId.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockWorld);
        Mock.VerifyNoOtherCalls(mockChat);
        Mock.VerifyNoOtherCalls(mockFamilyId);
    }

    [Test]
    public async Task OnCharacterLogin_ValidMembership_ReconcilesEnrollmentAchievement()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.EnrollFamily);
        data.AddAchievement(1000, 1, false);
        data.AddObjective(1, 1000, 100);

        var character = new CharacterMock
        {
            Id = 7,
            Name = "Member",
            Family = 9
        };
        var achievements = new CharacterAchievements(character, data.Build());
        s_achievementsField.SetValue(character, achievements);

        var member = new FamilyMember { Id = character.Id, Name = character.Name, Title = "" };
        var family = new Family { Id = character.Family };
        family.AddMember(member);

        var manager = new FamilyManager(
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IChatManager>().Object,
            Mock.Of<IFamilyIdManager>().Object);
        s_familiesField.SetValue(manager, new Dictionary<uint, Family> { [family.Id] = family });
        s_familyMembersField.SetValue(manager, new Dictionary<uint, FamilyMember> { [member.Id] = member });

        manager.OnCharacterLogin(character);

        await Assert.That(member.Character).IsEqualTo(character);
        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(1u);
    }
}
