using System.Numerics;

using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Housing;

public static class HousingPlacementRules
{
    public static ErrorMessageType Check(HousingAreaGameData data, WorldTemplate world, Vector3 position,
        uint categoryId, uint accountId, IEnumerable<House> houses)
    {
        if (world == null || !HousingAreaPolygon.IsFinite(position))
            return ErrorMessageType.HouseCannotLoacateInvalidCategoryArea;
        var areas = world.HousingZones.Values.SelectMany(polygons => polygons)
            .Where(polygon => polygon.Contains(position)).OrderByDescending(polygon => polygon.Priority).ToArray();
        if (areas.Length == 0)
            return ErrorMessageType.HouseCannotLoacateInvalidCategoryArea;
        var owned = houses.Where(h => h.AccountId == accountId).ToArray();
        // Equal-priority overlaps cannot relax a restrictive area's ownership rules.
        foreach (var polygon in areas.Where(p => p.Priority == areas[0].Priority).DistinctBy(p => p.Id))
        {
            var area = data.GetArea(polygon.Id);
            var group = area == null ? null : data.GetGroup(area.GroupId);
            if (group == null || !group.CategoryLimits.TryGetValue(categoryId, out var maximum))
                return ErrorMessageType.HouseCannotLoacateInvalidCategoryArea;
            if (group.Houseless && owned.Length > 0)
                return ErrorMessageType.HouseCannotOwnMoreHouselessCondition;
            if (group.ExistingCategoryId != 0 && owned.Any(h => h.Template.CategoryId == group.ExistingCategoryId))
                return ErrorMessageType.HouseCannotOwnMoreExistingCategoryCondition;
            if (maximum > 0 && owned.Count(h => h.Template.CategoryId == categoryId) >= maximum)
                return ErrorMessageType.HouseCannotConstructInAreaByMaxConstructCount;
        }
        return ErrorMessageType.NoErrorMessage;
    }
}
