using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSExecuteCraft() : GamePacket(CSOffsets.CSExecuteCraft, 1)
{
    public override void Read(PacketStream stream)
    {
        var craftId = stream.ReadUInt32();
        var objId = stream.ReadBc();
        var count = stream.ReadInt32();

        Logger.Debug("CSExecuteCraft, craftId : {0} , objId : {1}, count : {2}", craftId, objId, count);

        var character = Connection.ActiveChar;
        if (character == null)
            return;

        if (count is <= 0 or > CharacterCraft.MaxRequestedCraftCount)
        {
            Logger.Warn("Rejected invalid craft count {0} for craft {1} from character {2}", count, craftId, character.Id);
            character.SendErrorMessage(ErrorMessageType.CraftInvalidAmount);
            return;
        }

        if (!CraftManager.Instance.TryGetCraftById(craftId, out var craft))
        {
            Logger.Warn("Rejected unknown craft {0} from character {1}", craftId, character.Id);
            character.SendErrorMessage(ErrorMessageType.CraftInvalidCraftType);
            return;
        }

        character.Craft.Craft(craft, count, objId);
    }
}
