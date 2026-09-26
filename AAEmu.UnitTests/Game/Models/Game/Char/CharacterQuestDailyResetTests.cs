using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed class CharacterQuestDailyResetTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResetDailyQuests_PersistsWholeBlockBeforePackets(bool sendPackets)
    {
        var owner = new CharacterMock { Id = 279, Name = "DailyReset" };
        var session = Mock.Of<ISession>();
        owner.Connection = new GameConnection(session.Object) { ActiveChar = owner };
        var writes = 0;
        owner.Quests = new CharacterQuests(owner, block =>
        {
            writes++;
            if (block.Body[1] || block.Body[2] || !block.Body[3])
                throw new InvalidOperationException("Reset must persist both daily bits and preserve the normal quest.");
            if (!owner.Quests.IsQuestComplete(65) || !owner.Quests.IsQuestComplete(66))
                throw new InvalidOperationException("Reset published before persistence.");
            session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
            return true;
        }, _ => { });
        Seed(owner.Quests, 65, 66, 67);

        owner.Quests.ResetDailyQuests(sendPackets, id => new QuestTemplate
        {
            Id = id,
            DetailId = id == 67 ? QuestDetail.Normal : QuestDetail.Daily
        });

        await Assert.That(writes).IsEqualTo(1);
        await Assert.That(owner.Quests.IsQuestComplete(65)).IsFalse();
        await Assert.That(owner.Quests.IsQuestComplete(66)).IsFalse();
        await Assert.That(owner.Quests.IsQuestComplete(67)).IsTrue();
        session.SendPacket(Any<byte[]>()).WasCalled(sendPackets ? Times.Exactly(2) : Times.Never);
    }

    [Test]
    public async Task ResetDailyQuests_FailedBlock_KeepsBitsAndPacketsUntilRetry()
    {
        var owner = new CharacterMock { Id = 279, Name = "DailyReset" };
        var session = Mock.Of<ISession>();
        owner.Connection = new GameConnection(session.Object) { ActiveChar = owner };
        var allowWrite = false;
        owner.Quests = new CharacterQuests(owner, _ => allowWrite, _ => { });
        Seed(owner.Quests, 65, 66);
        QuestTemplate GetTemplate(uint id) => new() { Id = id, DetailId = QuestDetail.Daily };

        owner.Quests.ResetDailyQuests(true, GetTemplate);
        await Assert.That(owner.Quests.IsQuestComplete(65)).IsTrue();
        await Assert.That(owner.Quests.IsQuestComplete(66)).IsTrue();
        session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);

        allowWrite = true;
        owner.Quests.ResetDailyQuests(true, GetTemplate);
        owner.Quests.ResetDailyQuests(true, GetTemplate);
        await Assert.That(owner.Quests.IsQuestComplete(65)).IsFalse();
        await Assert.That(owner.Quests.IsQuestComplete(66)).IsFalse();
        session.SendPacket(Any<byte[]>()).WasCalled(Times.Exactly(2));
    }

    [Test]
    public async Task ResetDailyQuests_AllDailyKinds_SkipsActiveMissingAndNormalQuests()
    {
        var owner = new CharacterMock { Id = 279, Name = "DailyReset" };
        var writes = 0;
        owner.Quests = new CharacterQuests(owner, _ => { writes++; return true; }, _ => { });
        Seed(owner.Quests, 63, 64, 65, 66, 67, 68, 69);
        owner.Quests.ActiveQuests.Add(67, null);
        var kinds = new Dictionary<uint, QuestDetail>
        {
            [63] = QuestDetail.Daily, [64] = QuestDetail.DailyGroup,
            [65] = QuestDetail.DailyHunt, [66] = QuestDetail.DailyLivelihood,
            [67] = QuestDetail.Daily, [69] = QuestDetail.Normal
        };

        owner.Quests.ResetDailyQuests(false, id => kinds.TryGetValue(id, out var kind)
            ? new QuestTemplate { Id = id, DetailId = kind } : null);
        owner.Quests.ResetDailyQuests(false, id => kinds.TryGetValue(id, out var kind)
            ? new QuestTemplate { Id = id, DetailId = kind } : null);

        await Assert.That(writes).IsEqualTo(2);
        foreach (var id in new uint[] { 63, 64, 65, 66 })
            await Assert.That(owner.Quests.IsQuestComplete(id)).IsFalse();
        foreach (var id in new uint[] { 67, 68, 69 })
            await Assert.That(owner.Quests.IsQuestComplete(id)).IsTrue();
    }

    private static void Seed(CharacterQuests quests, params uint[] ids)
    {
        foreach (var id in ids)
            quests.SetCompletedQuestFlag(id, true, _ => true, out _, out _);
    }
}
