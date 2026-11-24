using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;

namespace AAEmu.Game.Models.Game.Housing;

public readonly record struct PlacedHousingDecoration(uint ItemTemplateId, HousingDecoration Design, bool Restore);

public static class HousingDecorationRules
{
    // The native special group counts each coffer. Other groups count distinct item types.
    public const uint StorageGroupId = 5;

    public static ErrorMessageType Check(HousingTemplate house, int constructionStep, int allDoodadCount,
        ItemHousingDecoration source, HousingDecoration candidate, IReadOnlyList<PlacedHousingDecoration> placed,
        HousingDecorationGameData data)
    {
        if (constructionStep != -1)
            return ErrorMessageType.HouseNotDecoratableState;
        if (allDoodadCount >= house.AbsoluteDecoLimit)
            return ErrorMessageType.HouseTooManyDecorations;
        var groupId = candidate.DecoActAbilityGroupId;
        if (groupId != 0 && CountGroup(placed, groupId) >= data.GetLimit(house.HousingDecoLimitId, groupId))
            return ErrorMessageType.AcatbilityDecoArrangFail;
        if (source.Restore && placed.Count(decoration => decoration.Restore) >= house.DecoLimit)
            return ErrorMessageType.NoMoreSpaceToDecorate;
        return ErrorMessageType.NoErrorMessage;
    }

    public static int CountGroup(IEnumerable<PlacedHousingDecoration> placed, uint groupId)
    {
        var matching = placed.Where(decoration => decoration.Design.DecoActAbilityGroupId == groupId);
        return groupId == StorageGroupId
            ? matching.Count()
            : matching.Select(decoration => decoration.ItemTemplateId).Distinct().Count();
    }

    public static List<PlacedHousingDecoration> GetPlaced(IEnumerable<Doodad> doodads, HousingGameData data)
    {
        var result = new List<PlacedHousingDecoration>();
        foreach (var doodad in doodads.OrderBy(doodad => doodad.DbId).ThenBy(doodad => doodad.ObjId))
        {
            if (GetDecoration(doodad, data) is { } decoration)
                result.Add(decoration);
        }
        return result;
    }

    internal static PlacedHousingDecoration? GetDecoration(Doodad doodad, HousingGameData data)
    {
        if (doodad.AttachPoint != AttachPointKind.None)
            return null;
        // Older furniture can lack its source item. A known seed or pack can share a furniture model.
        ItemHousingDecoration item;
        if (doodad.ItemTemplateId != 0)
            item = data.GetItemHousingDecorationByItem(doodad.ItemTemplateId);
        else
        {
            var legacyDesign = data.GetDecorationDesignFromDoodadId(doodad.TemplateId);
            item = legacyDesign == null ? null : data.GetItemHousingDecorations(legacyDesign.Id);
        }
        var design = item == null ? null : data.GetDecorationDesignFromId(item.DesignId);
        if (design == null || design.DoodadId != doodad.TemplateId)
            return null;
        return new PlacedHousingDecoration(item.ItemId, design, item.Restore);
    }

    public static uint GetActAbilityBonus(uint actabilityGroupId, HousingTemplate house,
        IEnumerable<PlacedHousingDecoration> placed, HousingDecorationGameData data)
    {
        var accepted = new List<PlacedHousingDecoration>();
        var itemTypes = new HashSet<uint>();
        var result = 0u;
        foreach (var decoration in placed)
        {
            if (!itemTypes.Add(decoration.ItemTemplateId))
                continue;
            var groupId = decoration.Design.DecoActAbilityGroupId;
            if (groupId != 0 && CountGroup(accepted, groupId) >= data.GetLimit(house.HousingDecoLimitId, groupId))
                continue;
            accepted.Add(decoration);
            if (decoration.Design.ActabilityGroupId == actabilityGroupId)
                result = checked(result + decoration.Design.ActabilityUp);
        }
        return result;
    }
}
