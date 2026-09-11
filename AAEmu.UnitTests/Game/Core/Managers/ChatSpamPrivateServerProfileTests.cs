using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>Synthetic private-server scenarios, not a measurement of real-traffic false positives.</summary>
public sealed class ChatSpamPrivateServerProfileTests
{
    [Test]
    public async Task Profile_PreservesDetectionThresholdsAndUsesThirtySecondMute()
    {
        var profile = Profile();
        await Assert.That(profile.Enabled).IsTrue();
        await Assert.That(profile.RateMessageCount).IsEqualTo(10U);
        await Assert.That(profile.RateWindowSeconds).IsEqualTo(5d);
        await Assert.That(profile.RepeatMessageCount).IsEqualTo(5U);
        await Assert.That(profile.RepeatWindowSeconds).IsEqualTo(60d);
        await Assert.That(profile.MinimumRepeatLength).IsEqualTo(10U);
        await Assert.That(profile.MuteSeconds).IsEqualTo(30d);
        await Assert.That(new ChatSpamConfig().MuteSeconds).IsEqualTo(600d);
    }

    [Test]
    [Arguments(ChatType.Party)]
    [Arguments(ChatType.Raid)]
    [Arguments(ChatType.Clan)]
    [Arguments(ChatType.Family)]
    [Arguments(ChatType.Whisper)]
    [Arguments(ChatType.Region)]
    public async Task OrdinaryConversation_StaysAllowed(ChatType channel)
    {
        await OrdinaryConversation(CreateInlineRules(), channel);
    }

    [Test]
    public async Task ShortCombatCalls_RepeatWithoutTriggeringTheLongMessageRule()
    {
        var time = new FakeTimeProvider();
        var manager = Manager(time, CreateInlineRules());
        var character = Character(1);
        for (var index = 0; index < 20; index++)
        {
            await Assert.That(manager.CheckMessage(character, ChatType.Party, "go").IsAllowed).IsTrue();
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Test]
    public async Task TradeRepeat_ExcludesTheMessageAtTheExactSixtySecondBoundary()
    {
        var time = new FakeTimeProvider();
        var manager = Manager(time, CreateInlineRules());
        var character = Character(1);
        const string message = "Selling iron ore at the auction house";
        for (var index = 0; index < 4; index++)
        {
            await Assert.That(manager.CheckMessage(character, ChatType.Trade, message).IsAllowed).IsTrue();
            time.Advance(TimeSpan.FromSeconds(10));
        }
        time.Advance(TimeSpan.FromSeconds(20));
        await Assert.That(manager.CheckMessage(character, ChatType.Trade, message).IsAllowed).IsTrue();
    }

    [Test]
    [Arguments(ChatSpamViolationType.RateLimit)]
    [Arguments(ChatSpamViolationType.RepeatedMessage)]
    [Arguments(ChatSpamViolationType.Rule)]
    public async Task Spam_RemainsBlockedAndTheAccountMuteEndsAtThirtySeconds(ChatSpamViolationType violation)
    {
        await SpamAndMute(CreateInlineRules(), violation);
    }

    [Test]
    public async Task RateWindow_ExcludesMessagesAtTheExactFiveSecondBoundary()
    {
        var time = new FakeTimeProvider();
        var manager = Manager(time, CreateInlineRules());
        var character = Character(1);
        for (var index = 0; index < 9; index++)
            await Assert.That(manager.CheckMessage(character, ChatType.Party, $"call {index}").IsAllowed).IsTrue();
        time.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(manager.CheckMessage(character, ChatType.Party, "ready").IsAllowed).IsTrue();
    }

    [Test]
    [Explicit]
    public async Task ActiveCompactAndReleaseProfile_PassSyntheticNormalAndSpamScenarios()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_CHAT_TEST_COMPACT");
        var profile = Environment.GetEnvironmentVariable("AAEMU_CHAT_TEST_PROFILE");
        Skip.Unless(!string.IsNullOrEmpty(compact) && !string.IsNullOrEmpty(profile),
            "Set AAEMU_CHAT_TEST_COMPACT and AAEMU_CHAT_TEST_PROFILE to the reviewed read-only release inputs.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = compact, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var rules = new ChatSpamGameData();
        rules.Load(connection);
        rules.PostLoad();
        await Assert.That(rules.ValidationIssues.Count).IsEqualTo(0);
        await Assert.That(rules.Rules.Count).IsEqualTo(1);
        await Profile_PreservesDetectionThresholdsAndUsesThirtySecondMute();
        foreach (var channel in new[] { ChatType.Party, ChatType.Raid, ChatType.Clan, ChatType.Trade, ChatType.Whisper })
            await OrdinaryConversation(rules, channel);
        foreach (var violation in new[] { ChatSpamViolationType.RateLimit, ChatSpamViolationType.RepeatedMessage, ChatSpamViolationType.Rule })
            await SpamAndMute(rules, violation);

        // Exercise every terminal keyword in the real graph, including Unicode spellings.
        var details = rules.Rules.Values.SelectMany(rule => rule.Details.Values).Where(detail => !detail.IsEndNode).ToArray();
        await Assert.That(details.Length).IsEqualTo(29);
        var time = new FakeTimeProvider();
        var manager = Manager(time, rules);
        foreach (var detail in details)
        {
            var result = manager.CheckMessage(Character(detail.Id), ChatType.Trade, $"Visit {detail.Text} now");
            await Assert.That(result.Violation).IsEqualTo(ChatSpamViolationType.Rule);
            await Assert.That(result.MutedUntil).IsEqualTo(time.GetUtcNow().AddSeconds(30));
        }
    }

    private static async Task OrdinaryConversation(ChatSpamGameData rules, ChatType channel)
    {
        var time = new FakeTimeProvider();
        var manager = Manager(time, rules);
        var character = Character(1);
        string[] messages =
        [
            "Hello Kegan, are you ready for the dungeon?",
            "I need to repair my equipment first.",
            "Mike can heal this group.",
            "Meet at the west gate in 2 minutes.",
            "I found a level 50 sword.",
            "The auction price is 25 gold.",
            "Can someone craft a trade pack?",
            "The quest target is beside the bridge.",
            "Thanks for the help.",
            "I will bring the ship to the coast.",
            "Selling iron ore for silver, send a whisper.",
            "Looking for 2 more players for the raid."
        ];
        foreach (var message in messages)
        {
            await Assert.That(manager.CheckMessage(character, channel, message).IsAllowed).IsTrue();
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task SpamAndMute(ChatSpamGameData rules, ChatSpamViolationType violation)
    {
        var time = new FakeTimeProvider();
        var audit = Mock.Of<ISusManager>();
        var manager = Manager(time, rules, audit.Object);
        var character = Character(1);
        ChatSpamCheckResult result;
        if (violation == ChatSpamViolationType.Rule)
        {
            result = manager.CheckMessage(character, ChatType.Trade, "Visit MMOCPU now");
        }
        else if (violation == ChatSpamViolationType.RateLimit)
        {
            for (var index = 0; index < 9; index++)
                await Assert.That(manager.CheckMessage(character, ChatType.Region, $"message {index}").IsAllowed).IsTrue();
            result = manager.CheckMessage(character, ChatType.Region, "message 9");
        }
        else
        {
            for (var index = 0; index < 4; index++)
            {
                await Assert.That(manager.CheckMessage(character, ChatType.Trade, "Repeated advertisement").IsAllowed).IsTrue();
                time.Advance(TimeSpan.FromSeconds(10));
            }
            result = manager.CheckMessage(character, ChatType.Trade, " REPEATED ADVERTISEMENT ");
        }
        await Assert.That(result.Violation).IsEqualTo(violation);
        await Assert.That(result.MutedUntil).IsEqualTo(time.GetUtcNow().AddSeconds(30));
        var sameAccount = Character(1);
        sameAccount.Id = 2;
        await Assert.That(manager.CheckMessage(sameAccount, ChatType.Whisper, "hello").Violation).IsEqualTo(ChatSpamViolationType.Muted);
        await Assert.That(manager.CheckMessage(Character(2), ChatType.Party, "hello").IsAllowed).IsTrue();
        time.Advance(TimeSpan.FromMilliseconds(29999));
        await Assert.That(manager.CheckMessage(character, ChatType.Party, "ready").Violation).IsEqualTo(ChatSpamViolationType.Muted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(manager.CheckMessage(character, ChatType.Party, "ready").IsAllowed).IsTrue();
        var category = violation == ChatSpamViolationType.Rule ? SusManager.CategoryRmt : SusManager.CategoryChatSpam;
        audit.LogActivity(category, character, Any<string>()).WasCalled(Times.Once);
    }

    private static ChatSpamManager Manager(FakeTimeProvider time, ChatSpamGameData rules, ISusManager audit = null) => new(
        audit ?? Mock.Of<ISusManager>().Object, time, Options.Create(new AppConfiguration { ChatSpam = Profile() }), rules);

    private static ChatSpamConfig Profile()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_CHAT_TEST_PROFILE");
        if (string.IsNullOrEmpty(path))
            return new ChatSpamConfig { MuteSeconds = 30 };
        using var config = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path), optional: false).Build();
        return config.GetSection("ChatSpam").Get<ChatSpamConfig>() ?? throw new InvalidDataException("Missing ChatSpam profile.");
    }

    private static CharacterMock Character(uint accountId) => new() { AccountId = accountId, Id = accountId, Name = $"Profile{accountId}" };

    private static ChatSpamGameData CreateInlineRules()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE chat_spam_rules (id INTEGER PRIMARY KEY, name TEXT);
            INSERT INTO chat_spam_rules VALUES (42,'inline-rmt');
            CREATE TABLE chat_spam_rule_details (
                id INTEGER PRIMARY KEY, chat_spam_rule_id INTEGER, text TEXT,
                detected_case_next_detail_id INTEGER, not_detected_case_next_detail_id INTEGER,
                start_node NUM, end_node NUM);
            INSERT INTO chat_spam_rule_details VALUES
                (100,42,'DETECTED',0,0,'f','t'),(101,42,'mmocpu',100,0,'t','f');
            """;
        command.ExecuteNonQuery();
        var rules = new ChatSpamGameData();
        rules.Load(connection);
        rules.PostLoad();
        return rules;
    }
}
