using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;

namespace AAEmu.UnitTests.Game.Models.Game.Expeditions;

[NotInParallel]
public sealed class ExpeditionAchievementTests
{
    private static readonly FieldInfo s_worldManagerInstanceField =
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_chatManagerInstanceField =
        typeof(Singleton<ChatManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_achievementsField =
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo s_guildChannelsProperty =
        typeof(ChatManager).GetProperty("GuildChannels", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public async Task OnCharacterLogin_ValidMembership_ReconcilesEnrollmentAchievement()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.EnrollGuild);
        data.AddAchievement(1000, 1, false);
        data.AddObjective(1, 1000, 100);

        var previousWorldManager = s_worldManagerInstanceField.GetValue(null);
        var previousChatManager = s_chatManagerInstanceField.GetValue(null);
        try
        {
            var worldManager = new WorldManager(
                Mock.Of<ITickManager>().Object,
                Mock.Of<IWorldIdManager>().Object,
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
            var chatManager = new ChatManager();
            s_worldManagerInstanceField.SetValue(null, worldManager);
            s_chatManagerInstanceField.SetValue(null, chatManager);

            var character = new CharacterMock { Id = 7, ObjId = 70, Name = "Member", Level = 20 };
            var achievements = new CharacterAchievements(character, data.Build());
            s_achievementsField.SetValue(character, achievements);
            worldManager.TryAddCharacter(character);

            var member = new ExpeditionMember { CharacterId = character.Id, Name = character.Name };
            var expedition = new Expedition
            {
                Id = (FactionsEnum)200,
                Name = "Guild",
                Members = [member]
            };
            var guildChannels =
                (ConcurrentDictionary<FactionsEnum, ChatChannel>)s_guildChannelsProperty.GetValue(chatManager)!;
            var channel = new ChatChannel { ChatType = ChatType.Clan, InternalName = expedition.Name };
            guildChannels[expedition.Id] = channel;

            expedition.OnCharacterLogin(character);
            expedition.OnCharacterLogin(character);

            await Assert.That(member.IsOnline).IsTrue();
            await Assert.That(channel.Members.Contains(character)).IsTrue();
            await Assert.That(achievements.GetAmount(1000)).IsEqualTo(1u);
        }
        finally
        {
            s_chatManagerInstanceField.SetValue(null, previousChatManager);
            s_worldManagerInstanceField.SetValue(null, previousWorldManager);
        }
    }
}
