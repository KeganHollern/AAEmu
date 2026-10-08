using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSHangPacket() : GamePacket(CSOffsets.CSHangPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // r208022 serializer 397bb360 writes exactly two u24 object IDs.
        if (stream.LeftBytes != 6)
            return;

        var unitObjId = stream.ReadBc();
        var targetObjId = stream.ReadBc();

        Logger.Trace($"Hang, unitObjId: {unitObjId}, targetObjId: {targetObjId}");
        var character = Connection?.ActiveChar;
        // The native producer always sends the local character, not another unit.
        if (character == null || unitObjId != character.ObjId || targetObjId == unitObjId)
            return;

        var target = character.ParentWorld?.GetGameObject(targetObjId);
        if (target == null)
            return;

        character.Transform.StickyParent = target.Transform;
        character.BroadcastPacket(new SCHungPacket(unitObjId, targetObjId), false);
        // Hang is an attachment message, not a skill-less loot/use request. Use(0)
        // restarts a tree's phase timers and can report climbing as crop theft.
    }
}
