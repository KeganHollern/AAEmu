using AAEmu.Game.Models.Game.CommonFarm.Static;

namespace AAEmu.Game.Models.Game.CommonFarm;

public sealed class CommonFarm
{
    public uint Id { get; init; }
    public string Name { get; init; }
    public FarmType Group { get; init; }
    public uint GuardTimeMilliseconds { get; init; }
}
