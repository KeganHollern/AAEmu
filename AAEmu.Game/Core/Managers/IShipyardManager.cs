using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Shipyard;

namespace AAEmu.Game.Core.Managers;

public interface IShipyardManager : ILoadable, IInitializable
{
    void Save(PersistenceSaveContext context);
    Shipyard Create(Character owner, ShipyardPlacementRequest request);
    void ShipyardCompletedTask(Shipyard shipyard);
}
