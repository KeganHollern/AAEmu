using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Tasks.Shipyard;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class CraftEffect : EffectTemplate
{
    public WorldInteractionType WorldInteraction { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        Logger.Debug("CraftEffect, {0}", WorldInteraction);

        var wiGroup = WorldManager.Instance.GetWorldInteractionGroup((uint)WorldInteraction);

        // Check what skill triggered this build
        uint usedSkill = 0;
        if (castObj is CastSkill castSkill)
            usedSkill = castSkill.SkillId;

        // Check for empty world interaction type
        /*
        if (wiGroup == null)
        {
            Logger.Warn($"CraftEffect, wi {WorldInteraction} does not not have wi group using skill {usedSkill}, defaulting to Collect");
            wiGroup = WorldInteractionGroup.Collect;
        }
        */

        if (caster is Character character)
        {
            // Logger.Warn($"{character.Name} triggered wiGroup {wiGroup}({(int)wiGroup}) with wi {WorldInteraction}({(int)WorldInteraction})");
            switch (wiGroup)
            {
                case WorldInteractionGroup.Craft:
                    if (target is Shipyard.Shipyard shipyard)
                    {
                        AdvanceShipyardConstruction(character, shipyard, usedSkill, source.Skill);
                    }
                    else
                    {
                        character.Craft.EndCraft();
                    }
                    break;
                case WorldInteractionGroup.Collect:
                    character.Craft.EndCraft();
                    break;
                case WorldInteractionGroup.Building when target is House house:
                    AdvanceHouseConstruction(character, house, usedSkill, source.Skill);
                    break;
                default:
                    Logger.Warn($"CraftEffect, {WorldInteraction} does not have a wi group ({wiGroup})");
                    if (target is Shipyard.Shipyard sy)
                    {
                        if (sy.ShipyardData.Type2 == character.Id)
                        {
                            CompleteShipyardConstruction(character, sy, source.Skill);
                        }
                        else
                        {
                            character.SendErrorMessage(ErrorMessageType.NoPermissionToLoot);
                            source.Skill.Cancelled = true;
                            SkillLaborBatch.Current?.Fail();
                        }
                    }
                    else
                    {
                        character.Craft.EndCraft();
                    }
                    break;
            }

            //character.Quests.OnInteraction(WorldInteraction, target);
            // инициируем событие
            //Task.Run(() => QuestManager.Instance.DoInteractionEvents((Character)caster, target.TemplateId));
            if (SkillLaborBatch.Current is { } batch)
                batch.AfterCommit(() => QuestManager.Instance.DoDoodadInteractionEvents((Character)caster, (Character)caster, target.TemplateId));
            else
                QuestManager.Instance.DoDoodadInteractionEvents((Character)caster, (Character)caster, target.TemplateId);
        }
    }

    internal static void AdvanceHouseConstruction(Character character, House house, uint usedSkill, Skill skill)
    {
        if (house.CurrentStep < 0 || (house.Template.BuildSteps.Count > 0 &&
            (!house.Template.BuildSteps.TryGetValue(house.CurrentStep, out var step) || step.SkillId != usedSkill)))
        {
            skill.Cancelled = true;
            SkillLaborBatch.Current?.Fail();
            return;
        }
        var batch = SkillLaborBatch.Current;
        if (batch != null)
            batch.Enlist(context =>
            {
                if (!house.Save(context))
                    throw new InvalidOperationException("Paid construction did not save its house row.");
            }, house.CaptureConstructionState());
        var previousStep = house.CurrentStep;
        house.AddBuildAction(batch == null);
        void PublishProgress()
        {
            if (batch != null && previousStep != house.CurrentStep)
                house.CompleteConstructionStepChange();
            character.BroadcastPacket(new SCHouseBuildProgressPacket(house.TlId, house.ModelId,
                house.AllAction, house.CurrentStep == -1 ? house.AllAction : house.CurrentAction), true);
            if (house.CurrentStep == -1)
            {
                foreach (var doodad in house.AttachedDoodads.ToArray())
                    doodad.Spawn();
                character.Achievements?.Increment(CharRecordKind.MakeHousing, house.TemplateId, 0);
            }
        }
        if (batch != null)
            batch.AfterCommit(PublishProgress);
        else
            PublishProgress();
    }

    internal static void AdvanceShipyardConstruction(Character character, Shipyard.Shipyard shipyard,
        uint usedSkill, Skill skill)
    {
        if (shipyard.CurrentStep < 0 ||
            !shipyard.Template.ShipyardSteps.TryGetValue(shipyard.CurrentStep, out var step) || step.SkillId != usedSkill)
        {
            skill.Cancelled = true;
            SkillLaborBatch.Current?.Fail();
            return;
        }
        var batch = SkillLaborBatch.Current;
        batch?.Enlist(null, shipyard.CaptureConstructionState());
        shipyard.AddBuildAction();
        shipyard.ShipyardData.Actions = shipyard.CurrentStep == -1 ? shipyard.AllAction : shipyard.CurrentAction;
        shipyard.ShipyardData.Step = shipyard.CurrentStep == -1
            ? shipyard.Template.ShipyardSteps.Count : shipyard.CurrentStep;
        void PublishProgress() => character.BroadcastPacket(new SCShipyardStatePacket(shipyard.ShipyardData), true);
        if (batch != null)
            batch.AfterCommit(PublishProgress);
        else
            PublishProgress();
        if (batch != null && character.Craft?.HasCurrentCraft != true)
            batch.AfterCommit(() => character.Craft?.EndCraft());
        else
            character.Craft?.EndCraft();
    }

    internal static void CompleteShipyardConstruction(Character character, Shipyard.Shipyard shipyard, Skill skill)
    {
        var batch = SkillLaborBatch.Current;
        if (shipyard.ShipyardData.Type2 != character.Id || shipyard.CurrentStep != -1 ||
            shipyard.ShipyardData.Step == 1000)
        {
            skill.Cancelled = true;
            batch?.Fail();
            return;
        }
        if (batch == null)
        {
            ShipyardManager.Instance.ShipyardCompletedTask(shipyard);
            return;
        }
        batch.Enlist(null, shipyard.CaptureConstructionState());
        if (!batch.Inventory.TryGrant(character.Inventory.Bag, shipyard.Template.ItemId, 1, 0))
        {
            batch.Fail();
            return;
        }
        shipyard.ShipyardData.Step = 1000;
        batch.AfterCommit(() =>
        {
            character.BroadcastPacket(new SCShipyardStatePacket(shipyard.ShipyardData), true);
            TaskManager.Instance.Schedule(new ShipyardCompleteTask { _shipyard = shipyard },
                TimeSpan.FromMilliseconds(shipyard.Template.CeremonyAnimTime));
        });
    }

}
