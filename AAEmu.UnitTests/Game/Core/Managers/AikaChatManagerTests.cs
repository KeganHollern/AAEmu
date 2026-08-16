using System.Text.Json;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class AikaChatManagerTests
{
    [Test]
    [Arguments("aika", true)]
    [Arguments("Aika?", true)]
    [Arguments("hey AIKA!", true)]
    [Arguments("not much, how about you aika?", true)]
    [Arguments("aika: hi", true)]
    [Arguments("kaika", false)]
    [Arguments("aikaton is my friend", false)]
    [Arguments("no bots here", false)]
    [Arguments("", false)]
    public async Task TriggerMatchesWholeWordCaseInsensitively(string message, bool expected)
    {
        var trigger = AikaChatManager.BuildTriggerRegex("aika");
        await Assert.That(trigger.IsMatch(message)).IsEqualTo(expected);
    }

    [Test]
    public async Task SanitizeReplyFlattensAndStripsDecorations()
    {
        var raw = "<think>secret plans</think>\n\"Aika: I-it's not like I wanted\nto help you or anything!\"  ";
        var reply = AikaChatManager.SanitizeReply(raw, "Aika", 240);
        await Assert.That(reply).IsEqualTo("I-it's not like I wanted to help you or anything!");
    }

    [Test]
    public async Task SanitizeReplyTruncatesAtWordBoundaryWithEllipsis()
    {
        var raw = string.Join(' ', Enumerable.Repeat("word", 100));
        var reply = AikaChatManager.SanitizeReply(raw, "Aika", 40);
        await Assert.That(reply.Length <= 40).IsTrue();
        await Assert.That(reply.EndsWith('\u2026')).IsTrue();
        await Assert.That(reply.Contains("word ")).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("<think>only thoughts</think>")]
    public async Task SanitizeReplyReturnsEmptyForUnusableOutput(string raw)
    {
        await Assert.That(AikaChatManager.SanitizeReply(raw, "Aika", 240)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task StripRepeatedPrefixDropsFullEcho()
    {
        const string prev = "Ew, no. He talks too much. It's not jealousy or anything.";
        await Assert.That(AikaChatManager.StripRepeatedPrefix(prev, prev)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task StripRepeatedPrefixKeepsOnlyTheNewTail()
    {
        const string prev = "Ew, no. He talks too much. It's not jealousy or anything. Just common sense.";
        const string echoed = prev + " And it's four!";
        await Assert.That(AikaChatManager.StripRepeatedPrefix(echoed, prev)).IsEqualTo("And it's four!");
    }

    [Test]
    [Arguments("Hmph. Fine, I'll help you this once.", "Something entirely different was said before.")]
    [Arguments("Ew, no way that works.", "Ew, no. He talks too much, seriously.")]
    [Arguments("short echo", "short")]
    public async Task StripRepeatedPrefixKeepsRepliesWithoutSubstantialEcho(string reply, string previous)
    {
        await Assert.That(AikaChatManager.StripRepeatedPrefix(reply, previous)).IsEqualTo(reply);
    }

    [Test]
    public async Task StripRepeatedPrefixIgnoresEmptyPrevious()
    {
        await Assert.That(AikaChatManager.StripRepeatedPrefix("Hmph.", "")).IsEqualTo("Hmph.");
    }

    [Test]
    public async Task RequestPayloadMapsHistoryRolesAndDisablesThinking()
    {
        var config = new AiChatConfig();
        var history = new List<AikaChatManager.ChatLine>
        {
            new("Lystic", "yo whats good?", false),
            new("Aika", "N-nothing! Go away.", true),
            new("Mike", "how about you aika?", false),
        };

        var payload = AikaChatManager.BuildRequestPayload(config, "Nuia", history);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        await Assert.That(root.GetProperty("model").GetString()).IsEqualTo("qwen");
        await Assert.That(root.GetProperty("max_tokens").GetInt32()).IsEqualTo(config.MaxTokens);
        await Assert.That(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()).IsFalse();

        var messages = root.GetProperty("messages");
        await Assert.That(messages.GetArrayLength()).IsEqualTo(4);
        await Assert.That(messages[0].GetProperty("role").GetString()).IsEqualTo("system");
        await Assert.That(messages[0].GetProperty("content").GetString()!.Contains("Nuia")).IsTrue();
        await Assert.That(messages[1].GetProperty("role").GetString()).IsEqualTo("user");
        await Assert.That(messages[1].GetProperty("content").GetString()).IsEqualTo("Lystic: yo whats good?");
        await Assert.That(messages[2].GetProperty("role").GetString()).IsEqualTo("assistant");
        await Assert.That(messages[2].GetProperty("content").GetString()).IsEqualTo("N-nothing! Go away.");
        await Assert.That(messages[3].GetProperty("role").GetString()).IsEqualTo("user");
    }

    [Test]
    public async Task CustomSystemPromptOverridesBuiltIn()
    {
        var config = new AiChatConfig { SystemPrompt = "You are a pirate." };
        await Assert.That(AikaChatManager.BuildSystemPrompt(config, "Pirate")).IsEqualTo("You are a pirate.");
    }

    [Test]
    public async Task ExtractContentReadsFirstChoice()
    {
        const string body = """
            {"choices":[{"message":{"role":"assistant","content":"Hmph. Fine."}}],"usage":{"total_tokens":10}}
            """;
        await Assert.That(AikaChatManager.ExtractContent(body)).IsEqualTo("Hmph. Fine.");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"choices\":[]}")]
    [Arguments("not json at all")]
    [Arguments("{\"choices\":[{\"message\":{\"content\":null}}]}")]
    public async Task ExtractContentIsSafeOnMalformedResponses(string body)
    {
        await Assert.That(AikaChatManager.ExtractContent(body)).IsEqualTo(string.Empty);
    }
}
