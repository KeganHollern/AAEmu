using AAEmu.Game.Models.Game.Music;

namespace AAEmu.UnitTests.Game.Models.Game.Music;

public sealed class MusicNoteRulesTests
{
    [Test]
    [Arguments("c", 1, 1)]
    [Arguments("é", 1, 2)]
    [Arguments("音", 1, 3)]
    [Arguments("\U0001D11E", 1, 4)]
    [Arguments("e\u0301", 2, 3)]
    [Arguments("c音\U0001D11E", 3, 8)]
    public async Task TryMeasure_UnicodeText_CountsCodePointsSeparatelyFromUtf8Bytes(
        string text, int expectedLength, int expectedBytes)
    {
        await Assert.That(MusicNoteRules.TryMeasure(text, out var length, out var bytes)).IsTrue();
        await Assert.That(length).IsEqualTo(expectedLength);
        await Assert.That(bytes).IsEqualTo(expectedBytes);
    }

    [Test]
    public async Task IsValid_MmlCommandsAndWhitespace_AllCharactersCountBeforeParsing()
    {
        const string song = "t120 o4 l8 c+,\r\n\t";
        const int length = 17;

        await Assert.That(MusicNoteRules.TryMeasure(song, out var actualLength, out var bytes)).IsTrue();
        await Assert.That(actualLength).IsEqualTo(length);
        await Assert.That(bytes).IsEqualTo(length);
        await Assert.That(MusicNoteRules.IsValid("Score", song, length)).IsTrue();
        await Assert.That(MusicNoteRules.IsValid("Score", song, length - 1)).IsFalse();
    }

    [Test]
    [Arguments("é")]
    [Arguments("音")]
    [Arguments("\U0001D11E")]
    public async Task IsValid_MultibyteSong_AppliesTheArtistryLimitToCodePoints(string character)
    {
        var song = string.Concat(Enumerable.Repeat(character, 200));

        await Assert.That(MusicNoteRules.IsValid("Score", song, 200)).IsTrue();
        await Assert.That(MusicNoteRules.IsValid("Score", song + character, 200)).IsFalse();
    }

    [Test]
    public async Task IsValid_CombiningMarks_CountSeparatelyWithoutNormalizingText()
    {
        var song = string.Concat(Enumerable.Repeat("e\u0301", 100));

        await Assert.That(MusicNoteRules.IsValid("Score", song, 200)).IsTrue();
        await Assert.That(MusicNoteRules.IsValid("Score", song + "\u0301", 200)).IsFalse();
    }

    [Test]
    [Arguments("null")]
    [Arguments("empty")]
    [Arguments("nul")]
    [Arguments("leading-nul")]
    [Arguments("interior-nul")]
    [Arguments("trailing-nul")]
    [Arguments("high-surrogate")]
    [Arguments("low-surrogate")]
    [Arguments("unpaired-high-surrogate")]
    [Arguments("reversed-surrogates")]
    public async Task IsValid_MissingOrMalformedText_RejectsBothTitleAndSong(string kind)
    {
        // Build malformed UTF-16 at runtime so attribute metadata cannot replace its code units.
        var text = kind switch
        {
            "null" => null,
            "empty" => "",
            "nul" => "\0",
            "leading-nul" => "\0c",
            "interior-nul" => "c\0d",
            "trailing-nul" => "c\0",
            "high-surrogate" => "\uD834",
            "low-surrogate" => "\uDD1E",
            "unpaired-high-surrogate" => "c\uD834d",
            "reversed-surrogates" => "\uDD1E\uD834",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        await Assert.That(MusicNoteRules.TryMeasure(text, out _, out _)).IsFalse();
        await Assert.That(MusicNoteRules.IsValid(text, "c", 200)).IsFalse();
        await Assert.That(MusicNoteRules.IsValid("Score", text, 200)).IsFalse();
    }

    [Test]
    [Arguments("c", 96)]
    [Arguments("é", 48)]
    [Arguments("音", 32)]
    [Arguments("\U0001D11E", 24)]
    public async Task IsValid_TitleBuffer_AcceptsNinetySixBytesAndRejectsOneMoreByte(
        string character, int repetitions)
    {
        var title = string.Concat(Enumerable.Repeat(character, repetitions));

        await Assert.That(MusicNoteRules.IsValid(title, "c", 200)).IsTrue();
        await Assert.That(MusicNoteRules.IsValid(title + "c", "c", 200)).IsFalse();
    }

    [Test]
    [Arguments("c", 12000)]
    [Arguments("é", 6000)]
    [Arguments("音", 4000)]
    [Arguments("\U0001D11E", 3000)]
    public async Task IsValid_SongBuffer_AcceptsTwelveThousandBytesAndRejectsOneMoreByte(
        string character, int repetitions)
    {
        var song = string.Concat(Enumerable.Repeat(character, repetitions));

        // Keep the note limit above both text lengths to isolate the native byte capacity.
        await Assert.That(MusicNoteRules.IsValid("Score", song, 12001)).IsTrue();
        await Assert.That(MusicNoteRules.IsValid("Score", song + "c", 12001)).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task IsValid_NonpositiveNoteLimit_RejectsNonemptyMusic(int noteLimit)
    {
        await Assert.That(MusicNoteRules.IsValid("Score", "c", noteLimit)).IsFalse();
    }
}
