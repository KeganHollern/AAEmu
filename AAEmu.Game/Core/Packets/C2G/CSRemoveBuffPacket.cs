using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSRemoveBuffPacket() : GamePacket(CSOffsets.CSRemoveBuffPacket, 1)
{
    private static bool RemoveEffect(Buff buffEffect)
    {
        if (buffEffect == null)
            return false;
        if (buffEffect.Template.Kind == BuffKind.Good)
            buffEffect.Exit();
        return true;
    }

    public override void Read(PacketStream stream)
    {
        var objId = stream.ReadBc();
        var buffId = stream.ReadUInt32();
        var reason = stream.ReadByte();

        // Check if it's actually the player
        if (Connection.ActiveChar.ObjId == objId)
        {
            var character = Connection.ActiveChar;
            var effect = character.Buffs.GetEffectByIndex(buffId);
            // The r208022 client ends an automatic dry landing with reason 9. Manual Fold uses its skill.
            if (reason == 9 && character.TryCloseGliderForLanding(effect))
                return;
            if (RemoveEffect(effect))
            {
                // Removed buff from player
                return;
            }
        }

        // TODO: check if player actually owns the pet
        var mate = Connection.ActiveChar.ParentWorld.MateManager.GetActiveMates(Connection.ActiveChar.Id).FirstOrDefault(x => x.Id == objId);
        if (mate != null)
        {
            if (RemoveEffect(mate.Buffs.GetEffectByIndex(buffId)))
            {
                // Removed buff from target pet
                return;
            }
        }

        // TODO: check if player actually owns the vehicle
        var slave = Connection.ActiveChar.ParentWorld.SlaveManager.GetSlaveByObjId(objId);
        if (slave != null)
        {
            // Removed buff from target vehicle
            if (RemoveEffect(slave.Buffs.GetEffectByIndex(buffId))) { return; }
        }

    }
}
