using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Duels;

namespace AAEmu.Game.Core.Managers;

public interface IDuelManager : IInitializable
{
    void DuelRequest(Character challenger, uint challengedId);
    void DuelAccepted(Character challenged, uint challengerId);
    void DuelStart(Duel duel);
    void DuelCancel(Character sender, uint challengerId, ErrorMessageType errorMessage);
    void CancelForCharacter(Character character);
}
