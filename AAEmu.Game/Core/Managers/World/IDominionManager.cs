using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Dominions;

namespace AAEmu.Game.Core.Managers.World;

public interface IDominionManager : ILoadable
{
    DominionState[] GetStates();
    void SendStates(Character character);
}
