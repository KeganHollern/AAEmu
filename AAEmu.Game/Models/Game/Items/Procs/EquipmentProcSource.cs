using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Items.Procs;

internal readonly record struct EquipmentProcKey(ulong ItemId, uint SetId, uint ProcId);

internal readonly record struct EquipmentProcSource(EquipmentProcKey Key, int ItemLevel)
{
    internal static IReadOnlyList<EquipmentProcSource> Collect(IEnumerable<Item> equipment, IItemManager items)
    {
        var equipped = equipment.OfType<EquipItem>().Where(item => item.IsNotDestroyed).ToArray();
        var sources = new Dictionary<EquipmentProcKey, EquipmentProcSource>();
        foreach (var item in equipped)
        {
            foreach (var procId in items.GetItemProcBindings(item.TemplateId))
                Add(item.Id, 0, procId, item.Template.Level);
            if (item.RuneId != 0)
                foreach (var procId in items.GetItemProcBindings(item.RuneId))
                    Add(item.Id, 0, procId, item.Template.Level);
            if (item.Template is WeaponTemplate { HoldableTemplate.ItemProcId: > 0 } weapon)
                Add(item.Id, 0, (uint)weapon.HoldableTemplate.ItemProcId, item.Template.Level);
        }

        foreach (var group in equipped.Where(item => item.Template is EquipItemTemplate { EquipItemSetId: > 0 })
                     .GroupBy(item => ((EquipItemTemplate)item.Template).EquipItemSetId))
        {
            var set = items.GetEquippedItemSet(group.Key);
            if (set == null)
                continue;
            var count = group.Count();
            var level = group.Min(item => item.Template.Level);
            foreach (var bonus in set.Bonuses.Where(bonus => bonus.ItemProcId != 0 && count >= bonus.NumPieces))
                Add(0, group.Key, bonus.ItemProcId, level);
        }

        return sources.Values.ToArray();

        void Add(ulong itemId, uint setId, uint procId, int level)
        {
            if (procId == 0)
                return;
            var key = new EquipmentProcKey(itemId, setId, procId);
            sources[key] = new EquipmentProcSource(key, level);
        }
    }
}
