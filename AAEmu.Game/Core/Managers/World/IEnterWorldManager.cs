using System.Net;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers.World;

public interface IEnterWorldManager
{
    void AddAccount(uint accountId, uint connectionId, uint token, ulong patronStart, ulong patronEnd, IPAddress address,
        ushort populationCap = 0, bool permitReservation = true);
    void Login(GameConnection connection, uint accountId, uint token);
    void Leave(GameConnection connection, LeaveWorldTargetType leaveWorldTargetType);
    void LeaveWorldTask(GameConnection connection, LeaveWorldTargetType leaveWorldTarget, Character activeChar);
}
