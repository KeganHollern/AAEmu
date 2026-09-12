using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.UnitTests.Utils.Mocks;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class CharacterManagerTests
{
    [Test]
    public async Task Constructor_DoesNotCallDeps()
    {
        var mockWorld = Mock.Of<IWorldManager>();
        var mockAccount = Mock.Of<IAccountManager>();
        var mockName = Mock.Of<INameManager>();
        var mockCharId = Mock.Of<ICharacterIdManager>();
        var mockFaction = Mock.Of<IFactionManager>();
        var mockSkill = Mock.Of<ISkillManager>();
        var mockItem = Mock.Of<IItemManager>();
        var mockHousing = Mock.Of<IHousingManager>();
        var mockFamily = Mock.Of<IFamilyManager>();
        var mockMail = Mock.Of<IMailManager>();
        var mockTask = Mock.Of<ITaskManager>();

        var manager = new CharacterManager(
            mockWorld.Object,
            mockAccount.Object,
            mockName.Object,
            mockCharId.Object,
            mockFaction.Object,
            mockSkill.Object,
            mockItem.Object,
            mockHousing.Object,
            mockFamily.Object,
            mockMail.Object,
            mockTask.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockWorld);
        Mock.VerifyNoOtherCalls(mockAccount);
        Mock.VerifyNoOtherCalls(mockName);
        Mock.VerifyNoOtherCalls(mockCharId);
        Mock.VerifyNoOtherCalls(mockFaction);
        Mock.VerifyNoOtherCalls(mockSkill);
        Mock.VerifyNoOtherCalls(mockItem);
        Mock.VerifyNoOtherCalls(mockHousing);
        Mock.VerifyNoOtherCalls(mockFamily);
        Mock.VerifyNoOtherCalls(mockMail);
        Mock.VerifyNoOtherCalls(mockTask);
    }
    [Test]
    public async Task GuildOwnerDeletion_RejectsRequestAndCleanupBeforeDatabaseOrAssets()
    {
        var mail = Mock.Of<IMailManager>();
        var housing = Mock.Of<IHousingManager>();
        var family = Mock.Of<IFamilyManager>();
        var task = Mock.Of<ITaskManager>();
        var manager = new CharacterManager(
            Mock.Of<IWorldManager>().Object, Mock.Of<IAccountManager>().Object,
            Mock.Of<INameManager>().Object, Mock.Of<ICharacterIdManager>().Object,
            Mock.Of<IFactionManager>().Object, Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemManager>().Object, housing.Object, family.Object, mail.Object, task.Object);
        var character = new CharacterMock { Id = 1, Name = "Owner" };
        character.Expedition = new Expedition { OwnerId = character.Id };
        var connection = new GameConnection(Mock.Of<ISession>().Object);
        connection.Characters.Add(character.Id, character);
        var oldRequest = character.DeleteRequestTime;

        manager.SetDeleteCharacter(connection, character.Id);
        manager.DeleteCharacterAssets(character, true);
        character.DeleteTime = DateTime.UtcNow.AddMinutes(-1);
        var deleted = manager.CheckForDeletedCharactersDeletion(character, connection, null);

        await Assert.That(character.DeleteRequestTime).IsEqualTo(oldRequest);
        await Assert.That(deleted).IsFalse();
        Mock.VerifyNoOtherCalls(mail);
        Mock.VerifyNoOtherCalls(housing);
        Mock.VerifyNoOtherCalls(family);
        Mock.VerifyNoOtherCalls(task);
    }
}
