using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSInteractNPCPacket() : GamePacket(CSOffsets.CSInteractNPCPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var objId = stream.ReadBc();
        var isTargetChanged = stream.ReadBoolean();

        Logger.Debug("InteractNPC, BcId: {0}, TargetChanged: {1}", objId, isTargetChanged);

        var unit = objId > 0 ? Connection.ActiveChar.ParentWorld.GetNpc(objId) : null;

        if (!ServiceInteraction.CanReach(Connection.ActiveChar, unit))
        {
            Connection.ActiveChar.CurrentInteractionObject = null;
            return;
        }

        Connection.ActiveChar.CurrentInteractionObject = unit;

        if (isTargetChanged)
        {
            Connection.ActiveChar.CurrentTarget = unit;
        }

        Connection.SendPacket(new SCAiAggroPacket(objId, 0)); // TODO проверить count=1
    }
}
