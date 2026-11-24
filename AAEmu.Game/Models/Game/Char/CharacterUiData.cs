using System.Text;
using AAEmu.Game.Models.Game.Quests;

namespace AAEmu.Game.Models.Game.Char;

/// <summary>r208022 server-backed UI slots and native SaveToBuffer payload capacity.</summary>
internal static class CharacterUiData
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal const int MaximumBytes = 8191;
    internal static bool IsServerKey(ushort key) => key is >= 1 and <= 5;

    internal static bool TryDecode(ushort key, ReadOnlySpan<byte> bytes, out string value)
    {
        value = null;
        if (bytes.Contains((byte)0))
            return false;

        try
        {
            // The native quest document can start with a quoted, single non-UTF-8 byte.
            // Its settings after that marker are UTF-8. A leading NUL marks this storage
            // form unambiguously because a native document cannot contain a NUL.
            if (key == QuestTrackerUiData.UiDataType && bytes.Length >= 5 && bytes[0] == '"' &&
                bytes[1] >= 0x80 && bytes[2..5].SequenceEqual("\"\r\n"u8))
                value = "\0" + (char)bytes[1] + StrictUtf8.GetString(bytes[2..]);
            else
                value = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static byte[] GetClientBytes(ushort key, string value)
    {
        if (key != QuestTrackerUiData.UiDataType ||
            value is not ['\0', >= '\u0080' and <= '\u00ff', '"', '\r', '\n', ..])
            return Encoding.UTF8.GetBytes(value);

        var text = value.AsSpan(2);
        var bytes = new byte[2 + Encoding.UTF8.GetByteCount(text)];
        bytes[0] = (byte)'"';
        bytes[1] = (byte)value[1];
        Encoding.UTF8.GetBytes(text, bytes.AsSpan(2));
        return bytes;
    }
}
