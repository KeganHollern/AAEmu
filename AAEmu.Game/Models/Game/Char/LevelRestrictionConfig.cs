using AAEmu.Game.Models.Game.Chat;

namespace AAEmu.Game.Models.Game.Char;

public sealed class LevelRestrictionConfig
{
    public byte AuctionSearch { get; set; } = 10;
    public byte AuctionBid { get; set; } = 10;
    public byte AuctionPost { get; set; } = 10;
    public byte Trade { get; set; } = 10;
    public byte Mail { get; set; } = 10;
    public byte Shout { get; set; } = 15;
    public byte TradeChat { get; set; } = 15;
    public byte GroupFind { get; set; } = 15;
    public byte Region { get; set; } = 15;
    public byte Ally { get; set; } = 15;

    public byte ChatLevel(ChatType type) => type switch
    {
        ChatType.Shout => Shout,
        ChatType.Trade => TradeChat,
        ChatType.GroupFind => GroupFind,
        ChatType.Region => Region,
        ChatType.Ally => Ally,
        _ => 0
    };

    public byte[] ChatLevels() => Enumerable.Range(0, 15).Select(value => ChatLevel((ChatType)value)).ToArray();

    internal static bool Check(Character character, byte minimum, ErrorMessageType error = ErrorMessageType.LevelLowToUse)
    {
        if (character == null)
            return false;
        if (character.Level >= minimum)
            return true;
        character.SendErrorMessage(error, minimum);
        return false;
    }
}
