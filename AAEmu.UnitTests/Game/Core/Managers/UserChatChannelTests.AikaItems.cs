using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class UserChatChannelTests
{
    [Test]
    public async Task FactionChat_ResolvesStockLinksOnlyInAikasRequest()
    {
        // The complete examples from stock r208022 x2ui/loot/loot_dice.alb.
        var grip = "|i24488,4,00000000002eNm001000004000000000000009400m" + new string('0', 183) + "P1LDG00000;";
        var lunadrop = "|i14698,3,00000000001gEG000m00004" + new string('0', 202) + "G000000000;";
        var sash = "|i24498,2,00000000002oNm000W00004000000000000000y00m" + new string('0', 183) + "G000000000;";
        const string gripFacts = "[Crushing Grip, Club, requires level 45]";
        const string lunadropFacts = "[Radiant Fire Lunadrop, Lunastone, use skill Finishing Touches by Moonlight]";
        const string sashFacts = "[Exiled Wizard's Sash, Cloth Armor, requires level 45]";
        const string question = " what is this item used for?";
        var longLine = new string('a', 250);
        // The native writer encodes 192 bytes into 256 characters. '*' and '+' belong to its alphabet.
        var nativeLink = "|i24488,4," + new string('*', 128) + new string('+', 128) + ";";
        (string Broadcast, string Context)[] lines =
        [
            (grip + question, gripFacts + question),
            (lunadrop + question, lunadropFacts + question),
            (sash + question, sashFacts + question),
            ($"Compare {grip} with {sash}", $"Compare {gripFacts} with {sashFacts}"),
            (grip.Replace("24488", "999999") + question, "[Unknown item #999999]" + question),
            ("literal |i24488,4,short;", "literal |i24488,4,short;"),
            ("|i24488,4,unfinished", "|i24488,4,unfinished"),
            (longLine, longLine[..200]),
            (nativeLink + " Aika," + question, gripFacts + " Aika," + question)
        ];

        SetUpAikaItemTemplates();
        var owner = Character(1);
        var faction = new ChatChannel { ChatType = ChatType.Ally, Faction = FactionsEnum.NuiaAlliance, InternalName = "Nuia" };
        var channels = (ConcurrentDictionary<FactionsEnum, ChatChannel>)typeof(ChatManager)
            .GetProperty("FactionChannels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_chat)!;
        channels[faction.Faction] = faction;
        faction.JoinChannel(owner);
        owner.Session.Packets.Clear();

        using var sink = new RecordingAikaRequest();
        using var client = new HttpClient(sink);
        var aika = new AikaChatManager();
        const BindingFlags privateField = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AikaChatManager).GetField("_httpClient", privateField)!.SetValue(aika, client);
        typeof(AikaChatManager).GetField("_trigger", privateField)!.SetValue(aika, AikaChatManager.BuildTriggerRegex("aika"));
        SetInstance(aika);
        var originalConfig = AppConfiguration.Instance.AiChat;
        AppConfiguration.Instance.AiChat = new AiChatConfig { Enabled = true };
        try
        {
            new CSSendChatMessagePacket { Connection = owner.Connection }.Read(SendBody((ulong)ChatType.White, "Aika, local chat"));
            foreach (var (broadcast, _) in lines)
            {
                await Assert.That(sink.Requests).IsEmpty();
                var sent = owner.Session.Packets.Count;
                new CSSendChatMessagePacket { Connection = owner.Connection }.Read(SendBody((ulong)ChatType.Ally, broadcast));
                var expected = new SCChatMessagePacket(ChatType.Ally, owner, broadcast, 0, 0).Write(new PacketStream());
                await Assert.That(Body(owner.Session.Packets[sent]).GetBytes().SequenceEqual(expected.GetBytes())).IsTrue();
            }

            using var request = JsonDocument.Parse(sink.Requests.Single());
            var messages = request.RootElement.GetProperty("messages");
            await Assert.That(messages.GetArrayLength()).IsEqualTo(lines.Length + 1);
            for (var index = 0; index < lines.Length; index++)
            {
                var message = messages[index + 1];
                await Assert.That(message.GetProperty("role").GetString()).IsEqualTo("user");
                await Assert.That(message.GetProperty("content").GetString()).IsEqualTo($"{owner.Name}: {lines[index].Context}");
            }
        }
        finally
        {
            AppConfiguration.Instance.AiChat = originalConfig;
        }
    }

    private void SetUpAikaItemTemplates()
    {
        var texts = new LocalizationManager();
        var templates = new Dictionary<uint, ItemTemplate>();
        void Add(uint id, string name, int category, string categoryName, int requiredLevel, uint useSkill = 0)
        {
            templates.Add(id, new ItemTemplate { Id = id, CategoryId = category, LevelRequirement = requiredLevel, UseSkillId = useSkill });
            texts.AddTranslation("items", "name", id, name);
            texts.AddTranslation("item_categories", "name", category, categoryName);
        }
        Add(24488, "Crushing Grip", 74, "Club", 45);
        Add(14698, "Radiant Fire Lunadrop", 20, "Lunastone", 0, 11285);
        Add(24498, "Exiled Wizard's Sash", 83, "Cloth Armor", 45);
        texts.AddTranslation("skills", "name", 11285, "Finishing Touches by Moonlight");
        var items = new ItemManager(null, null, null, texts, null, null);
        typeof(ItemManager).GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(items, templates);
        SetInstance(items);
        SetInstance(texts);
    }

    private sealed class RecordingAikaRequest : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"OK"}}]}""")
            };
        }
    }
}
