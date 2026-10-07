using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSChangeDoodadPhasePacket() : GamePacket(CSOffsets.CSChangeDoodadPhasePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // r208022 sends this after a local bubble/book interaction, without starting a skill.
        // Native serializer 397bbbb0: object u24, skill u32, next phase i32, function row u32.
        if (stream.LeftBytes != 15)
            return;

        var objId = stream.ReadBc();
        var skillId = stream.ReadUInt32();
        var nextPhase = stream.ReadInt32();
        var funcKey = stream.ReadUInt32();
        TryChangePhase(Connection?.ActiveChar, objId, skillId, nextPhase, funcKey);
    }

    internal static bool TryChangePhase(Character character, uint objId, uint skillId, int nextPhase, uint funcKey)
    {
        if (objId == 0 || skillId == 0 || nextPhase <= 0 || funcKey == 0)
            return false;

        lock (SaveManager.PersistenceSyncRoot)
        {
            var world = character?.ParentWorld;
            var doodad = world?.GetDoodad(objId);
            if (doodad == null || doodad.Despawn > DateTime.MinValue ||
                character.Transform == null || doodad.Transform == null ||
                !ReferenceEquals(world, doodad.ParentWorld) ||
                character.Transform.InstanceId != doodad.Transform.InstanceId ||
                character.Transform.WorldId != doodad.Transform.WorldId)
                return false;

            // The client identifies an authored transition. It cannot select an arbitrary phase.
            // The active row also rejects a duplicate request after the phase changes.
            var func = doodad.CurrentFuncs.FirstOrDefault(candidate =>
                candidate.GroupId == doodad.FuncGroupId && candidate.FuncKey == funcKey &&
                candidate.SkillId == skillId && candidate.NextPhase == nextPhase &&
                candidate.FuncType is nameof(DoodadFuncBubble) or nameof(DoodadFuncOpenPaper));
            if (func == null)
                return false;

            // Use the normal authored skill range. A fixed service distance rejects
            // large objects whose interaction skills allow up to 20 metres.
            var template = SkillManager.Instance.GetSkillTemplate(func.SkillId);
            if (template == null || SkillRange.Check(new Skill(template), character, doodad) != SkillResult.Success ||
                !DoodadPermissionRules.Demand(character, doodad, func.PermId))
                return false;

            doodad.DoChangePhase(character, func.NextPhase);
            return true;
        }
    }
}
