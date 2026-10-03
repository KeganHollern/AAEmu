using System.Buffers;
using System.Text;

using AAEmu.Game.GameData;

namespace AAEmu.Game.Models.Game.Names;

public static class NameRules
{
    private enum Locale { Korean, Chinese, English, Japanese, Taiwanese, Russian, German, French }

    private readonly record struct Policy(int LocalMinimum, int Maximum, int AsciiMinimum,
        bool AllowSpace = false, bool AllowMixedCase = false, int SpaceLimit = 0, bool AllowSpecial = false);

    public static bool IsWellFormed(string name)
    {
        return TryMeasure(name, out _, out _);
    }

    public static NameValidationResult Validate(string name, NameType type, string locale = null, NameGameData data = null)
    {
        if (string.IsNullOrEmpty(name))
            return NameValidationResult.Length;
        if (!TryGetLocale(locale ?? AppConfiguration.Instance.DefaultLanguage, out var selectedLocale) ||
            !Enum.IsDefined(type))
            return NameValidationResult.Characters;

        var policy = GetPolicy(selectedLocale, type);
        var firstIsAscii = IsAsciiLetter(name[0]);
        var minimum = firstIsAscii ? policy.AsciiMinimum : policy.LocalMinimum;
        if (!TryMeasure(name, out var length, out var bytes))
            return NameValidationResult.Characters;

        // Native name buffers hold 128 bytes. Family titles have a 104-byte payload.
        var maximumBytes = type == NameType.FamilyTitle ? 104 : 128;
        if (length < minimum || length > policy.Maximum || bytes > maximumBytes)
            return NameValidationResult.Length;
        // The English/Russian client indexes the final UTF-8 byte incorrectly for
        // multibyte names. Check the actual last character before a server write.
        if (name[0] is '.' or ' ' || name[^1] == ' ')
            return NameValidationResult.Characters;

        var snapshot = (data ?? NameGameData.Instance).Data;
        var spaces = 0;
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (character == ' ')
            {
                if (!policy.AllowSpace || policy.SpaceLimit > 0 && ++spaces > policy.SpaceLimit)
                    return NameValidationResult.Characters;
                continue;
            }

            if (!AllowedGlyph(character, selectedLocale, firstIsAscii, policy.AllowSpecial))
                return NameValidationResult.Characters;
            var checksCase = selectedLocale is Locale.English or Locale.German or Locale.French or Locale.Russian ||
                firstIsAscii;
            if (index > 0 && !policy.AllowMixedCase && checksCase && character < 0x800 && char.IsUpper(character))
                return NameValidationResult.Characters;

            // The native faction producer alone adds the allowed_name_chars check.
            if (type == NameType.Faction && !IsAsciiLetter(character) &&
                !snapshot.AllowedCharacters.Contains(character))
                return NameValidationResult.Characters;
        }

        return IsReserved(name, snapshot) ? NameValidationResult.Reserved : NameValidationResult.Valid;
    }

    private static bool TryMeasure(string name, out int length, out int bytes)
    {
        length = 0;
        bytes = 0;
        if (string.IsNullOrEmpty(name))
            return false;
        var remaining = name.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var character, out var consumed) != OperationStatus.Done ||
                character.Value > char.MaxValue || Rune.IsControl(character))
                return false;
            length++;
            bytes += character.Utf8SequenceLength;
            remaining = remaining[consumed..];
        }

        return true;
    }

    public static bool IsReserved(string name, NameGameData data = null)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return IsReserved(name, (data ?? NameGameData.Instance).Data);
    }

    private static bool IsReserved(string name, NameGameData.Snapshot snapshot)
    {
        // r208022 uses _stricmp / _strnicmp over UTF-8. The authored name rows are
        // ASCII. Do not extend their matches through Unicode or culture folding.
        var foldedName = FoldAscii(name);
        foreach (var blocked in snapshot.BlockedNames)
        {
            var text = FoldAscii(blocked.Text);
            if (blocked.PartialMatch ? foldedName.Contains(text, StringComparison.Ordinal) : foldedName == text)
                return true;
        }

        return false;
    }

    private static bool TryGetLocale(string name, out Locale locale)
    {
        locale = name?.ToLowerInvariant() switch
        {
            "ko" => Locale.Korean,
            "zh_cn" => Locale.Chinese,
            "en_us" => Locale.English,
            "ja" => Locale.Japanese,
            "zh_tw" => Locale.Taiwanese,
            "ru" => Locale.Russian,
            "de" => Locale.German,
            "fr" => Locale.French,
            _ => (Locale)(-1)
        };
        return Enum.IsDefined(locale);
    }

    private static Policy GetPolicy(Locale locale, NameType type)
    {
        // XlGetNamePolicyInfo, r208022 xlcommon.dll, table at 0x3305db78.
        if (locale is Locale.English or Locale.German or Locale.French)
        {
            return type switch
            {
                NameType.Character or NameType.Summon => new(2, 26, 2),
                NameType.Faction => new(2, 32, 3, true, true),
                NameType.FamilyTitle => new(2, 26, 2, AllowMixedCase: true),
                NameType.ChatTab => new(2, 10, 2, true, true),
                _ => new(3, 32, 3, true, true)
            };
        }
        if (locale == Locale.Russian)
        {
            return type switch
            {
                NameType.Character or NameType.FamilyTitle => new(2, 18, 2),
                NameType.Summon => new(2, 18, 2, true, true, 1),
                NameType.ChatTab => new(2, 10, 2, true, true, 1),
                _ => new(2, 21, 2, true, true, 1)
            };
        }
        if (locale == Locale.Japanese)
        {
            return type switch
            {
                NameType.Character => new(2, 12, 1, true, true),
                NameType.Faction => new(1, 21, 1, true, true),
                NameType.ChatTab => new(1, 5, 1, true, true, AllowSpecial: true),
                _ => new(1, 12, 1, true, true)
            };
        }
        return type switch
        {
            NameType.Character or NameType.Summon => new(2, 12, 3),
            NameType.Faction => new(2, 21, 3, true, true),
            NameType.FamilyTitle => new(1, 12, 1, AllowMixedCase: true),
            NameType.ChatTab => new(1, 5, 1, true, true, AllowSpecial: true),
            _ => new(1, 12, 1, true, true, AllowSpecial: locale == Locale.Korean)
        };
    }

    private static bool AllowedGlyph(char character, Locale locale, bool firstIsAscii, bool allowSpecial)
    {
        if (allowSpecial)
            return true;
        if (locale is Locale.English or Locale.German or Locale.French)
            return character is >= '\u0020' and <= '\u007f' or >= '\u00a0' and <= '\u017f';
        if (firstIsAscii)
            return IsAsciiLetter(character);
        return locale switch
        {
            Locale.Chinese => character is >= '\u4e00' and <= '\u9fff',
            Locale.Japanese => true,
            Locale.Russian => character is >= '\u0410' and <= '\u044f' or '\u0401' or '\u0451',
            _ => character is >= '\uac00' and <= '\ud7af'
        };
    }

    private static bool IsAsciiLetter(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string FoldAscii(string text)
    {
        return string.Create(text.Length, text, static (result, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                result[index] = character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
            }
        });
    }
}
