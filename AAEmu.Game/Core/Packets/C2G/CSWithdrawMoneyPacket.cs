using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSWithdrawMoneyPacket() : GamePacket(CSOffsets.CSWithdrawMoneyPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var amount = stream.ReadInt32();
        var aapoint = stream.ReadInt32();

        Logger.Debug("WithdrawMoney: amount -> {0}, aa_point -> {1}", amount, aapoint);

        if (!ServiceInteraction.CanUseBank(Connection.ActiveChar))
        {
            Connection.ActiveChar.SendErrorMessage(ErrorMessageType.NoInteractionAvailable);
            return;
        }
        if (amount <= 0 || aapoint != 0)
            return;

        Connection.ActiveChar.ChangeMoney(SlotType.Bank, SlotType.Inventory, amount);
    }
}
