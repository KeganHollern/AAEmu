using System.Buffers;
using System.Text;

namespace AAEmu.Game.Models.Game.Music;

public static class MusicNoteRules
{
    // r208022 stores native NUL-terminated buffers of 97 and 12001 bytes.
    public const int MaximumTitleBytes = 96;
    public const int MaximumSongBytes = 12000;

    public static bool IsValid(string title, string song, int noteLimit)
    {
        return TryMeasure(title, out _, out var titleBytes) && titleBytes <= MaximumTitleBytes &&
            TryMeasure(song, out var length, out var songBytes) && length <= noteLimit &&
            songBytes <= MaximumSongBytes;
    }

    public static bool TryMeasure(string text, out int length, out int utf8Bytes)
    {
        length = 0;
        utf8Bytes = 0;
        if (string.IsNullOrEmpty(text))
            return false;

        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                rune.Value == 0)
                return false;
            length++;
            utf8Bytes += rune.Utf8SequenceLength;
            remaining = remaining[consumed..];
        }

        return true;
    }
}
