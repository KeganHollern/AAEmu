using AAEmu.Game.Core.Network.Connections;

namespace AAEmu.Game.Core.Managers.World;

public interface IStreamManager
{
    uint AddToken(GameConnection connection);
    void RemoveToken(GameConnection connection);
    void Login(StreamConnection connection, uint accountId, uint token);
}
