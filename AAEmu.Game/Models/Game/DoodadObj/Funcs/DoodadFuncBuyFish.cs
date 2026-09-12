using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncBuyFish : DoodadFuncTemplate
{
    // doodad_funcs
    public uint ItemId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncBuyFish");

        if (caster is not Character character || owner == null)
            return;

        lock (SaveManager.PersistenceSyncRoot)
        {
            var backpack = character.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack);
            if (backpack == null)
            {
                character.SendErrorMessage(ErrorMessageType.StoreBackpackNogoods);
                return;
            }

            if (backpack.Template == null || backpack.Template.Refund < 0)
            {
                character.SendErrorMessage(ErrorMessageType.BagInvalidItem);
                return;
            }

            var exchanged = false;
            var batch = SkillLaborBatch.For(character);
            using (var ownMutation = batch == null ? new InventoryMutation(ItemTaskType.SkillEffectConsumption) : null)
            {
                var mutation = batch?.Inventory ?? ownMutation;
                if (mutation.TryConsume(character.Equipment, backpack, backpack.Count) &&
                    mutation.TryChangeMoney(character, backpack.Template.Refund))
                {
                    // Display the sold pack only after both sides of the exchange are ready.
                    owner.ItemTemplateId = backpack.TemplateId;
                    if (batch == null)
                        mutation.Complete();
                    exchanged = true;
                }
            }

            // Failed preparation is disposed before publishing a failure to the player.
            if (!exchanged)
            {
                batch?.Fail();
                character.SendErrorMessage(ErrorMessageType.BagInvalidItem);
            }
        }
    }
}
