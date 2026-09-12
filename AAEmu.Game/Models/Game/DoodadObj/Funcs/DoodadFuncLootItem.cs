using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncLootItem : DoodadFuncTemplate
{
    private readonly Random _random;

    // doodad_funcs
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public WorldInteractionType WorldInteractionId { get; set; }
    public uint ItemId { get; init; }
    public int CountMin { get; init; }
    public int CountMax { get; init; }
    public int Percent { get; init; }
    public int RemainTime { get; init; }
    public uint GroupId { get; init; }

    public DoodadFuncLootItem() : this(Random.Shared)
    {
    }

    internal DoodadFuncLootItem(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
    }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (owner == null)
            return;

        owner.ToNextPhase = false;
        if (caster is not Character character)
            return;

        Logger.Debug($"DoodadFuncLootItem: skillId {skillId}, nextPhase {nextPhase}, ItemId {ItemId}, CountMin {CountMin}, CountMax {CountMax}, Percent {Percent}, RemainTime {RemainTime}, GroupId {GroupId}");

        if (CountMin < 0 || CountMax < CountMin || Percent is < 0 or > 10000)
        {
            character.SendErrorMessage(ErrorMessageType.BagInvalidItem);
            return;
        }

        // Percent is a count of successful outcomes among 10,000 equally likely rolls.
        if (_random.Next(0, 10000) >= Percent)
            return;

        // Compact data includes fixed counts and 0..1 ranges: both endpoints are inclusive.
        var count = (int)_random.NextInt64(CountMin, (long)CountMax + 1);
        if (count == 0)
            return;

        var granted = ItemId == Item.Coins
            ? character.AddMoney(SlotType.Inventory, count)
            : character.Inventory.TryAddNewItem(ItemTaskType.RecoverDoodadItem, ItemId, count);

        if (granted)
        {
            void RecordLoot() => character.Achievements?.Increment(CharRecordKind.GetLootitem, ItemId, 0, (uint)count);
            if (SkillLaborBatch.For(character) is { } batch)
                batch.AfterCommit(RecordLoot);
            else
                RecordLoot();
        }

        if (!granted)
            character.SendErrorMessage(ErrorMessageType.BagInvalidItem);

        // Move to next phase only when loot was actually granted.
        owner.ToNextPhase = granted;
    }
}
