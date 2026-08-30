using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSExpressEmotionPacket() : GamePacket(CSOffsets.CSExpressEmotionPacket, 1)
{
    private const float MaxNpcEmotionRange = 20f;

    public override void Read(PacketStream stream)
    {
        var characterObjId = stream.ReadBc();  // character
        var npcObjId = stream.ReadBc(); // target
        var emotionId = stream.ReadUInt32();

        Logger.Warn("ExpressEmotion, ObjId: {0}, Obj2Id: {1}, EmotionId: {2}", characterObjId, npcObjId, emotionId);
        var character = Connection?.ActiveChar;
        if (character == null || character.ObjId != characterObjId)
            return;

        character.BroadcastPacket(new SCEmotionExpressedPacket(characterObjId, npcObjId, emotionId), true);

        var npc = character.ParentWorld?.GetNpc(npcObjId);
        if (npc is { IsVisible: true } &&
            character.UnitIsVisible(npc) &&
            character.CanSeeTarget(npc) &&
            character.GetDistanceTo(npc, true) <= MaxNpcEmotionRange)
        {
            character.Achievements.Increment(CharRecordKind.NpcEmotion, npc.TemplateId, emotionId);
        }

        //Connection?.ActiveChar?.Quests?.OnExpressFire(emotionId, characterObjId, npcObjId);
        // инициируем событие
        //Task.Run(() => QuestManager.Instance.DoOnExpressFireEvents(Connection.ActiveChar, emotionId, characterObjId, npcObjId));
        var animId = ExpressTextManager.Instance.GetExpressAnimId(emotionId);
        QuestManager.Instance.DoOnExpressFireEvents(character, animId, characterObjId, npcObjId);
    }
}
