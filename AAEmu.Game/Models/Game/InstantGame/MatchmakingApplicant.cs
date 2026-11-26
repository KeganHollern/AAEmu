using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.InstantGame;

public class MatchmakingApplicant(Character charObj, ZoneInstanceId declinedRoom = null)
{
    public DateTime TimeApplied { get; } = DateTime.UtcNow;
    public ZoneInstanceId DeclinedRoom { get; } = declinedRoom;
    public Character CharObj { get; } = charObj;
}