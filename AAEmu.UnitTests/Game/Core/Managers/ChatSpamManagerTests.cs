using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class ChatSpamManagerTests
{
    [Test]
    public async Task CheckMessage_AtRateThreshold_MutesAndQueuesForReview()
    {
        var timeProvider = new FakeTimeProvider();
        var susManager = Mock.Of<ISusManager>();
        var manager = CreateManager(timeProvider, susManager.Object, new ChatSpamConfig
        {
            RateMessageCount = 3,
            RateWindowSeconds = 5,
            RepeatMessageCount = 0,
            MuteSeconds = 30
        });
        var character = CreateCharacter(1, 10, "RateTester");

        var first = manager.CheckMessage(character, ChatType.White, "first");
        var second = manager.CheckMessage(character, ChatType.White, "second");
        var third = manager.CheckMessage(character, ChatType.White, "third");

        await Assert.That(first.IsAllowed).IsTrue();
        await Assert.That(second.IsAllowed).IsTrue();
        await Assert.That(third.Violation).IsEqualTo(ChatSpamViolationType.RateLimit);
        await Assert.That(third.ErrorMessage).IsEqualTo(ErrorMessageType.YellTooOften);
        susManager.LogActivity(
                SusManager.CategoryChatSpam,
                character,
                Is<string>(description => description.Contains("3 messages in 5 seconds")))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task CheckMessage_AfterRateWindowExpires_DoesNotCountExpiredMessages()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(timeProvider, Mock.Of<ISusManager>().Object, new ChatSpamConfig
        {
            RateMessageCount = 3,
            RateWindowSeconds = 5,
            RepeatMessageCount = 0,
            MuteSeconds = 30
        });
        var character = CreateCharacter(1, 10, "WindowTester");

        manager.CheckMessage(character, ChatType.White, "first");
        manager.CheckMessage(character, ChatType.White, "second");
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var result = manager.CheckMessage(character, ChatType.White, "third");

        await Assert.That(result.IsAllowed).IsTrue();
    }

    [Test]
    public async Task CheckMessage_NormalizedRepeatedMessagesAtThreshold_MutesSender()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(timeProvider, Mock.Of<ISusManager>().Object, new ChatSpamConfig
        {
            RateMessageCount = 0,
            RepeatMessageCount = 3,
            RepeatWindowSeconds = 30,
            MinimumRepeatLength = 10,
            MuteSeconds = 30
        });
        var character = CreateCharacter(1, 10, "RepeatTester");

        var first = manager.CheckMessage(character, ChatType.White, "Buy gold from us");
        var second = manager.CheckMessage(character, ChatType.White, " BUY GOLD FROM US ");
        var third = manager.CheckMessage(character, ChatType.White, "buy gold from us");

        await Assert.That(first.IsAllowed).IsTrue();
        await Assert.That(second.IsAllowed).IsTrue();
        await Assert.That(third.Violation).IsEqualTo(ChatSpamViolationType.RepeatedMessage);
    }

    [Test]
    public async Task CheckMessage_RepeatedMessageBelowMinimumLength_RemainsAllowed()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(timeProvider, Mock.Of<ISusManager>().Object, new ChatSpamConfig
        {
            RateMessageCount = 0,
            RepeatMessageCount = 3,
            RepeatWindowSeconds = 30,
            MinimumRepeatLength = 10,
            MuteSeconds = 30
        });
        var character = CreateCharacter(1, 10, "ShortTester");

        manager.CheckMessage(character, ChatType.White, "short");
        manager.CheckMessage(character, ChatType.White, "short");
        var result = manager.CheckMessage(character, ChatType.White, "short");

        await Assert.That(result.IsAllowed).IsTrue();
    }

    [Test]
    public async Task CheckMessage_RepeatThresholdOne_RejectsFirstEligibleMessage()
    {
        var manager = CreateManager(new FakeTimeProvider(), Mock.Of<ISusManager>().Object, new ChatSpamConfig
        {
            RateMessageCount = 0,
            RepeatMessageCount = 1,
            RepeatWindowSeconds = 30,
            MinimumRepeatLength = 10,
            MuteSeconds = 30
        });
        var character = CreateCharacter(1, 10, "ThresholdTester");

        var result = manager.CheckMessage(character, ChatType.White, "eligible message");

        await Assert.That(result.Violation).IsEqualTo(ChatSpamViolationType.RepeatedMessage);
    }

    [Test]
    public async Task CheckMessage_AfterRepeatWindowExpires_DoesNotCountExpiredMessages()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(timeProvider, Mock.Of<ISusManager>().Object, new ChatSpamConfig
        {
            RateMessageCount = 0,
            RepeatMessageCount = 3,
            RepeatWindowSeconds = 5,
            MinimumRepeatLength = 10,
            MuteSeconds = 30
        });
        var character = CreateCharacter(1, 10, "RepeatWindowTester");

        var first = manager.CheckMessage(character, ChatType.White, "repeated message");
        var second = manager.CheckMessage(character, ChatType.White, " REPEATED MESSAGE ");
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var result = manager.CheckMessage(character, ChatType.White, "Repeated Message");

        await Assert.That(first.IsAllowed).IsTrue();
        await Assert.That(second.IsAllowed).IsTrue();
        await Assert.That(result.IsAllowed).IsTrue();
    }

    [Test]
    public async Task CheckMessage_AfterMuteExpires_AllowsMessage()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(
            timeProvider,
            Mock.Of<ISusManager>().Object,
            DisabledLimits(muteSeconds: 10),
            CreateGameData("mmocpu"));
        var character = CreateCharacter(1, 10, "MuteTester");

        var violation = manager.CheckMessage(character, ChatType.White, "Visit MMOCPU now");
        var muted = manager.CheckMessage(character, ChatType.White, "ordinary message");
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        var expired = manager.CheckMessage(character, ChatType.White, "ordinary message");

        await Assert.That(violation.Violation).IsEqualTo(ChatSpamViolationType.Rule);
        await Assert.That(muted.Violation).IsEqualTo(ChatSpamViolationType.Muted);
        await Assert.That(expired.IsAllowed).IsTrue();
    }

    [Test]
    public async Task CheckMessage_OneAccountIsMuted_OtherAccountRemainsAllowed()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(timeProvider, Mock.Of<ISusManager>().Object, new ChatSpamConfig
        {
            RateMessageCount = 2,
            RateWindowSeconds = 5,
            RepeatMessageCount = 0,
            MuteSeconds = 30
        });
        var firstAccount = CreateCharacter(1, 10, "FirstAccount");
        var secondAccount = CreateCharacter(2, 20, "SecondAccount");

        manager.CheckMessage(firstAccount, ChatType.White, "first");
        var violation = manager.CheckMessage(firstAccount, ChatType.White, "second");
        var otherAccount = manager.CheckMessage(secondAccount, ChatType.White, "first");

        await Assert.That(violation.Violation).IsEqualTo(ChatSpamViolationType.RateLimit);
        await Assert.That(otherAccount.IsAllowed).IsTrue();
    }

    [Test]
    public async Task CheckMessage_TwoCharactersOnSameAccount_ShareRateLimitAndMute()
    {
        var timeProvider = new FakeTimeProvider();
        var manager = CreateManager(timeProvider, Mock.Of<ISusManager>().Object, new ChatSpamConfig
        {
            RateMessageCount = 2,
            RateWindowSeconds = 5,
            RepeatMessageCount = 0,
            MuteSeconds = 30
        });
        var firstCharacter = CreateCharacter(1, 10, "FirstCharacter");
        var secondCharacter = CreateCharacter(1, 20, "SecondCharacter");

        var first = manager.CheckMessage(firstCharacter, ChatType.White, "first");
        var violation = manager.CheckMessage(secondCharacter, ChatType.White, "second");
        var muted = manager.CheckMessage(firstCharacter, ChatType.White, "third");

        await Assert.That(first.IsAllowed).IsTrue();
        await Assert.That(violation.Violation).IsEqualTo(ChatSpamViolationType.RateLimit);
        await Assert.That(muted.Violation).IsEqualTo(ChatSpamViolationType.Muted);
    }

    [Test]
    public async Task CheckMessage_ConcurrentCallsCrossingRateThreshold_QueuesOneAuditAndMutesRemainder()
    {
        const int rateThreshold = 3;
        const int callCount = 10;
        var timeProvider = new FakeTimeProvider();
        var susManager = Mock.Of<ISusManager>();
        var manager = CreateManager(timeProvider, susManager.Object, new ChatSpamConfig
        {
            RateMessageCount = rateThreshold,
            RateWindowSeconds = 5,
            RepeatMessageCount = 0,
            MuteSeconds = 30
        });
        var character = CreateCharacter(1, 10, "ConcurrentTester");
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, callCount)
            .Select(index => Task.Run(() =>
            {
                start.Wait();
                return manager.CheckMessage(character, ChatType.White, $"message {index}");
            }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        await Assert.That(results.Count(result => result.IsAllowed)).IsEqualTo(rateThreshold - 1);
        await Assert.That(results.Count(result => result.Violation == ChatSpamViolationType.RateLimit)).IsEqualTo(1);
        await Assert.That(results.Count(result => result.Violation == ChatSpamViolationType.Muted))
            .IsEqualTo(callCount - rateThreshold);
        susManager.LogActivity(SusManager.CategoryChatSpam, character, Any<string>()).WasCalled(Times.Once);
    }

    [Test]
    public async Task CheckMessage_CompactRuleMatch_UsesRmtAuditCategoryAndRuleMetadata()
    {
        var timeProvider = new FakeTimeProvider();
        var susManager = Mock.Of<ISusManager>();
        var manager = CreateManager(
            timeProvider,
            susManager.Object,
            DisabledLimits(muteSeconds: 30),
            CreateGameData("mmocpu"));
        var character = CreateCharacter(1, 10, "RuleTester");

        var result = manager.CheckMessage(character, ChatType.Trade, "Visit MMOCPU now");

        await Assert.That(result.Violation).IsEqualTo(ChatSpamViolationType.Rule);
        await Assert.That(result.ErrorMessage).IsEqualTo(ErrorMessageType.SaidForbiddenWord);
        await Assert.That(result.RuleId).IsEqualTo(42u);
        await Assert.That(result.DetailId).IsEqualTo(101u);
        susManager.LogActivity(
                SusManager.CategoryRmt,
                character,
                Is<string>(description =>
                    description.Contains("rule 42 (test-rule) detail 101") &&
                    description.Contains("in Trade")))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task CheckMessage_WhileMuted_DoesNotQueueAdditionalAuditEntries()
    {
        var timeProvider = new FakeTimeProvider();
        var susManager = Mock.Of<ISusManager>();
        var manager = CreateManager(
            timeProvider,
            susManager.Object,
            DisabledLimits(muteSeconds: 30),
            CreateGameData("mmocpu"));
        var character = CreateCharacter(1, 10, "AuditTester");

        var violation = manager.CheckMessage(character, ChatType.White, "mmocpu");
        var firstMutedAttempt = manager.CheckMessage(character, ChatType.White, "ordinary message");
        var secondMutedAttempt = manager.CheckMessage(character, ChatType.White, "mmocpu again");

        await Assert.That(violation.Violation).IsEqualTo(ChatSpamViolationType.Rule);
        await Assert.That(firstMutedAttempt.Violation).IsEqualTo(ChatSpamViolationType.Muted);
        await Assert.That(secondMutedAttempt.Violation).IsEqualTo(ChatSpamViolationType.Muted);
        susManager.LogActivity(SusManager.CategoryRmt, character, Any<string>()).WasCalled(Times.Once);
        susManager.LogActivity(SusManager.CategoryChatSpam, character, Any<string>()).WasCalled(Times.Never);
    }

    private static ChatSpamManager CreateManager(
        FakeTimeProvider timeProvider,
        ISusManager susManager,
        ChatSpamConfig config,
        ChatSpamGameData gameData = null)
    {
        gameData ??= CreateGameData();
        return new ChatSpamManager(
            susManager,
            timeProvider,
            Options.Create(new AppConfiguration { ChatSpam = config }),
            gameData);
    }

    private static ChatSpamConfig DisabledLimits(double muteSeconds)
    {
        return new ChatSpamConfig
        {
            RateMessageCount = 0,
            RepeatMessageCount = 0,
            MuteSeconds = muteSeconds
        };
    }

    private static CharacterMock CreateCharacter(uint accountId, uint characterId, string name)
    {
        return new CharacterMock
        {
            AccountId = accountId,
            Id = characterId,
            Name = name
        };
    }

    private static ChatSpamGameData CreateGameData(string keyword = null)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
                            CREATE TABLE chat_spam_rules (id INTEGER PRIMARY KEY, name TEXT);
                            CREATE TABLE chat_spam_rule_details (
                                id INTEGER PRIMARY KEY,
                                chat_spam_rule_id INTEGER,
                                text TEXT,
                                detected_case_next_detail_id INTEGER,
                                not_detected_case_next_detail_id INTEGER,
                                start_node NUM,
                                end_node NUM
                            );
                            """);

        if (keyword != null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO chat_spam_rules (id, name) VALUES (42, 'test-rule');
                                  INSERT INTO chat_spam_rule_details
                                      (id, chat_spam_rule_id, text, detected_case_next_detail_id,
                                       not_detected_case_next_detail_id, start_node, end_node)
                                  VALUES
                                      (100, 42, 'DETECTED', 0, 0, 'f', 't'),
                                      (101, 42, @keyword, 100, 0, 't', 'f');
                                  """;
            command.Parameters.AddWithValue("@keyword", keyword);
            command.ExecuteNonQuery();
        }

        var gameData = new ChatSpamGameData();
        gameData.Load(connection);
        gameData.PostLoad();
        return gameData;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
