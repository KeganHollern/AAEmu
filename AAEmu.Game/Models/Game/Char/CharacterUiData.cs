namespace AAEmu.Game.Models.Game.Char;

/// <summary>r208022 server-backed UI slots and native SaveToBuffer payload capacity.</summary>
internal static class CharacterUiData
{
    internal const int MaximumBytes = 8191;
    internal static bool IsServerKey(ushort key) => key is >= 1 and <= 5;
}
