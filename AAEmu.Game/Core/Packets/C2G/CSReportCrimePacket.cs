using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSReportCrimePacket() : GamePacket(CSOffsets.CSReportCrimePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        if (stream.LeftBytes < 17)
            return;
        var objId = stream.ReadBc();
        var skillId = stream.ReadUInt32();
        var doodadNextFuncGroup = stream.ReadInt32();
        var doodadFuncId = stream.ReadUInt32();
        var messageLength = stream.ReadUInt16();
        if (messageLength > 200 || messageLength != stream.LeftBytes)
            return;
        var msg = stream.ReadString(messageLength);

        var reporter = Connection.ActiveChar;

        var bloodStainDoodad = reporter.ParentWorld?.GetDoodad(objId);
        if (bloodStainDoodad != null)
        {
            var crimeEvent = CrimeManager.Instance.ReportCrime(reporter, bloodStainDoodad, skillId, doodadNextFuncGroup, doodadFuncId, msg);
            if (crimeEvent != null)
            {
                var criminalName = NameManager.Instance.GetCharacterName(bloodStainDoodad.OwnerId) ?? string.Empty;
                Logger.Debug($"ReportCrime, ObjId: {objId}, Msg: {msg}, SkillId: {skillId}, DoodadFuncGroup: {doodadNextFuncGroup}, DoodadFuncId: {doodadFuncId} (0x{doodadFuncId:X8}). Owner {criminalName} ({bloodStainDoodad.OwnerId}), OwnerDbId {bloodStainDoodad.OwnerDbId}");
            }
            else
            {
                Logger.Debug($"ReportCrime, No crime record created ObjId: {objId}, Msg: {msg}");
            }
        }
        else
        {
            Logger.Warn($"ReportCrime, Invalid evidence ObjId: {objId}, Msg: {msg}");
        }
    }
}
