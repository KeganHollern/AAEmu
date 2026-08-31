using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class InteractionEffect : EffectTemplate
{
    public WorldInteractionType WorldInteraction { get; set; }
    public uint DoodadId { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        Logger.Debug("InteractionEffect, {0}", WorldInteraction);

        var classType = Type.GetType("AAEmu.Game.Models.Game.World.Interactions." + WorldInteraction);
        if (classType == null)
        {
            Logger.Error("InteractionEffect, Unknown world interaction: {0}", WorldInteraction);
            return;
        }

        Logger.Debug("InteractionEffect, Action: {0}", classType); // TODO help to debug...

        caster.Buffs.TriggerRemoveOn(Buffs.BuffRemoveOn.Interaction);

        var action = (IWorldInteraction)Activator.CreateInstance(classType);
        ExecuteWorldInteraction(action, caster, casterObj, target, targetObj, source, DoodadId);

        if (caster is not Character character) { return; }
        if (character.SkillCancelled) { return; }
        if (caster is Character && target is Doodad doodad)
        {
            //character.Quests.OnInteraction(WorldInteraction, target);
            // инициируем событие
            //Task.Run(() => QuestManager.Instance.DoInteractionEvents((Character)caster, target.TemplateId));
            QuestManager.Instance.DoDoodadInteractionEvents((Character)caster, (Character)caster, target.TemplateId);
        }
    }

    internal static void ExecuteWorldInteraction(IWorldInteraction action, BaseUnit caster, SkillCaster casterObj,
        BaseUnit target, SkillCastTarget targetObj, EffectSource source, uint doodadId,
        Func<uint, uint, DoodadFunc> doodadFuncResolver = null)
    {
        if (action == null || source is not { Skill: { } skill } || casterObj == null || target == null ||
            targetObj == null || skill.Template == null)
        {
            return;
        }

        if (caster is Character && target is Doodad doodad)
        {
            doodadFuncResolver ??= DoodadManager.Instance.GetFunc;
            var doodadFunc = doodadFuncResolver(doodad.FuncGroupId, skill.Id);
            if (doodadFunc?.FuncType == nameof(DoodadFuncExitIndun))
            {
                // ExitIndun loads the saved world before normal skill cleanup runs.
                caster.BroadcastPacket(new SCSkillEndedPacket(skill.TlId), true);
            }
        }

        action.Execute(caster, casterObj, target, targetObj, skill.Template.Id, doodadId);
    }
}
