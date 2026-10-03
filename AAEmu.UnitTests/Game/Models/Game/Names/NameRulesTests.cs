using System.Globalization;

using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Names;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Names;

public sealed class NameRulesTests
{
    [Test]
    [Arguments("Admin", true)]
    [Arguments("ADMIN", true)]
    [Arguments("TheAdmin", true)]
    [Arguments("Administrator", true)]
    [Arguments("GM", true)]
    [Arguments("gm", true)]
    [Arguments("Gmster", false)]
    [Arguments("Chatword", false)]
    [Arguments("AdMİN", false)]
    [Arguments("ſtaff", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task IsReserved_NameRows_UsesAsciiCaseAndTheAuthoredMatchKind(string name, bool expected)
    {
        var data = CreateData();

        await Assert.That(NameRules.IsReserved(name, data)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("en-US")]
    [Arguments("tr-TR")]
    public async Task IsReserved_ProcessCulture_DoesNotChangeAsciiMatching(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var data = CreateData();

            await Assert.That(NameRules.IsReserved("ADMIN", data)).IsTrue();
            await Assert.That(NameRules.IsReserved("AdMİN", data)).IsFalse();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    [Arguments("ko", NameType.Character, 3, 12)]
    [Arguments("ko", NameType.Summon, 3, 12)]
    [Arguments("ko", NameType.Faction, 3, 21)]
    [Arguments("ko", NameType.FamilyTitle, 1, 12)]
    [Arguments("ko", NameType.ChatTab, 1, 5)]
    [Arguments("ko", NameType.Portal, 1, 12)]
    [Arguments("zh_cn", NameType.Character, 3, 12)]
    [Arguments("zh_cn", NameType.Summon, 3, 12)]
    [Arguments("zh_cn", NameType.Faction, 3, 21)]
    [Arguments("zh_cn", NameType.FamilyTitle, 1, 12)]
    [Arguments("zh_cn", NameType.ChatTab, 1, 5)]
    [Arguments("zh_cn", NameType.Portal, 1, 12)]
    [Arguments("en_us", NameType.Character, 2, 26)]
    [Arguments("en_us", NameType.Summon, 2, 26)]
    [Arguments("en_us", NameType.Faction, 3, 32)]
    [Arguments("en_us", NameType.FamilyTitle, 2, 26)]
    [Arguments("en_us", NameType.ChatTab, 2, 10)]
    [Arguments("en_us", NameType.Portal, 3, 32)]
    [Arguments("ja", NameType.Character, 1, 12)]
    [Arguments("ja", NameType.Summon, 1, 12)]
    [Arguments("ja", NameType.Faction, 1, 21)]
    [Arguments("ja", NameType.FamilyTitle, 1, 12)]
    [Arguments("ja", NameType.ChatTab, 1, 5)]
    [Arguments("ja", NameType.Portal, 1, 12)]
    [Arguments("zh_tw", NameType.Character, 3, 12)]
    [Arguments("zh_tw", NameType.Summon, 3, 12)]
    [Arguments("zh_tw", NameType.Faction, 3, 21)]
    [Arguments("zh_tw", NameType.FamilyTitle, 1, 12)]
    [Arguments("zh_tw", NameType.ChatTab, 1, 5)]
    [Arguments("zh_tw", NameType.Portal, 1, 12)]
    [Arguments("ru", NameType.Character, 2, 18)]
    [Arguments("ru", NameType.Summon, 2, 18)]
    [Arguments("ru", NameType.Faction, 2, 21)]
    [Arguments("ru", NameType.FamilyTitle, 2, 18)]
    [Arguments("ru", NameType.ChatTab, 2, 10)]
    [Arguments("ru", NameType.Portal, 2, 21)]
    [Arguments("de", NameType.Character, 2, 26)]
    [Arguments("de", NameType.Summon, 2, 26)]
    [Arguments("de", NameType.Faction, 3, 32)]
    [Arguments("de", NameType.FamilyTitle, 2, 26)]
    [Arguments("de", NameType.ChatTab, 2, 10)]
    [Arguments("de", NameType.Portal, 3, 32)]
    [Arguments("fr", NameType.Character, 2, 26)]
    [Arguments("fr", NameType.Summon, 2, 26)]
    [Arguments("fr", NameType.Faction, 3, 32)]
    [Arguments("fr", NameType.FamilyTitle, 2, 26)]
    [Arguments("fr", NameType.ChatTab, 2, 10)]
    [Arguments("fr", NameType.Portal, 3, 32)]
    public async Task Validate_AsciiName_UsesTheNativeLocaleAndTypeLengthBounds(string locale, NameType type, int minimum, int maximum)
    {
        var data = CreateData();

        await Assert.That(NameRules.Validate(new string('a', minimum), type, locale, data)).IsEqualTo(NameValidationResult.Valid);
        await Assert.That(NameRules.Validate(new string('a', maximum), type, locale, data)).IsEqualTo(NameValidationResult.Valid);
        await Assert.That(NameRules.Validate(new string('a', minimum - 1), type, locale, data)).IsEqualTo(NameValidationResult.Length);
        await Assert.That(NameRules.Validate(new string('a', maximum + 1), type, locale, data)).IsEqualTo(NameValidationResult.Length);
    }

    [Test]
    [Arguments("en_us", NameType.Character, "Àb", true)]
    [Arguments("en_us", NameType.Character, "Aç", true)]
    [Arguments("en_us", NameType.Character, "aÇ", false)]
    [Arguments("en_us", NameType.Character, "Ab9", true)]
    [Arguments("en_us", NameType.Character, "Ab<", true)]
    [Arguments("en_us", NameType.Character, "Ab\\", true)]
    [Arguments("en_us", NameType.Character, "\u00a0a", true)]
    [Arguments("en_us", NameType.Character, "A\u00a0", true)]
    [Arguments("en_us", NameType.Character, "Ab Cd", false)]
    [Arguments("en_us", NameType.Character, ".ab", false)]
    [Arguments("en_us", NameType.Character, "Аб", false)]
    [Arguments("en_us", NameType.Faction, "Good Guild", true)]
    [Arguments("en_us", NameType.Faction, "Good  Guild", true)]
    [Arguments("en_us", NameType.Faction, " Good Guild", false)]
    [Arguments("en_us", NameType.Faction, "Good Guild ", false)]
    [Arguments("en_us", NameType.Faction, "Àbc", false)]
    [Arguments("en_us", NameType.Faction, "Guild9", false)]
    [Arguments("en_us", NameType.Faction, "Guild!", false)]
    [Arguments("en_us", NameType.FamilyTitle, "BigSister", true)]
    [Arguments("en_us", NameType.FamilyTitle, "Big Sister", false)]
    [Arguments("en_us", NameType.Portal, "My Place", true)]
    [Arguments("en_us", NameType.Portal, "Éva ", false)]
    [Arguments("en_us", NameType.Character, "Admin", false)]
    [Arguments("en_us", NameType.FamilyTitle, "ADMIN", false)]
    [Arguments("en_us", NameType.Portal, "The Admin", false)]
    [Arguments("ko", NameType.Character, "가가", true)]
    [Arguments("ko", NameType.Character, "가a", false)]
    [Arguments("ko", NameType.Portal, "A가", true)]
    [Arguments("ko", NameType.Faction, "가가", true)]
    [Arguments("ko", NameType.Faction, "나나", false)]
    [Arguments("zh_cn", NameType.Character, "中文", true)]
    [Arguments("zh_tw", NameType.Character, "가가", true)]
    [Arguments("zh_tw", NameType.Character, "中文", false)]
    [Arguments("ja", NameType.Character, "あA", true)]
    [Arguments("ja", NameType.Character, "Aあ", false)]
    [Arguments("ru", NameType.Character, "Аб", true)]
    [Arguments("ru", NameType.Character, "Ёж", true)]
    [Arguments("ru", NameType.Character, "аБ", false)]
    [Arguments("ru", NameType.Character, "Аb", false)]
    [Arguments("ru", NameType.Summon, "А Б", true)]
    [Arguments("ru", NameType.Summon, "А Б В", false)]
    [Arguments("unknown", NameType.Character, "Ab", false)]
    public async Task Validate_NativeGlyphAndNameRules_UsesTheConfirmedBranch(string locale, NameType type, string name, bool expected)
    {
        await Assert.That(NameRules.Validate(name, type, locale, CreateData()) == NameValidationResult.Valid).IsEqualTo(expected);
    }

    [Test]
    [Arguments("A\0b")]
    [Arguments("A\nb")]
    [Arguments("A\rb")]
    [Arguments("A\tb")]
    [Arguments("A\u007fb")]
    [Arguments("A\u0085b")]
    [Arguments("high-surrogate")]
    [Arguments("low-surrogate")]
    [Arguments("A😀b")]
    public async Task Validate_UnsafeUnicode_RejectsEveryNameType(string name)
    {
        name = name switch { "high-surrogate" => "A\ud800b", "low-surrogate" => "A\udc00b", _ => name };
        foreach (var type in Enum.GetValues<NameType>())
            await Assert.That(NameRules.Validate(name, type, "ja", CreateData())).IsEqualTo(NameValidationResult.Characters);
        await Assert.That(NameRules.IsWellFormed(name)).IsFalse();
    }

    private static NameGameData CreateData()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE allowed_name_chars (id INTEGER, char TEXT, bytes INTEGER);
            CREATE TABLE blocked_texts
                (id INTEGER, utf8str TEXT, bytes INTEGER, check_name TEXT, check_chat TEXT, partial_match TEXT);
            INSERT INTO allowed_name_chars VALUES (1, 'a', 1), (2, '가', 3);
            INSERT INTO blocked_texts VALUES
                (1, 'Admin', 5, 't', 'f', 't'),
                (2, 'GM', 2, 't', 'f', 'f'),
                (3, 'Chatword', 8, 'f', 't', 'f'),
                (4, 'staff', 5, 't', 'f', 'f');
            """;
        command.ExecuteNonQuery();
        var data = new NameGameData();
        data.Load(connection);
        return data;
    }
}
