using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.Game.Models.Game.Housing;

public readonly record struct HousingConstructionPose(Vector3 Position, float Yaw, ErrorMessageType Error)
{
    public bool IsValid => Error == ErrorMessageType.NoErrorMessage;
}

/// <summary>Pure r208022 placement pose rules. World collision and area rules run separately.</summary>
public static class HousingConstructionGeometry
{
    public static bool IsPlotInsideAreas(HousingTemplate template, Vector3 position,
        Func<Vector3, HousingAreaPolygon> findArea)
    {
        if (template.GardenRadius <= 0)
            return true;
        var cells = HousingFootprint.GetCells(new Vector2(position.X, position.Y), template.GardenRadius, template.Alley);
        if (cells.Count == 0)
            return false;
        foreach (var cell in cells)
        {
            // Native39331ba0 selects the first corner's area and checks the other three against that same area.
            var corners = cell.Corners(0);
            var area = findArea(corners[0]);
            if (area == null || corners.Skip(1).Any(corner => !area.Contains(corner)))
                return false;
        }
        return true;
    }

    public static bool OverlapsHouse(HousingTemplate candidate, CryBounds candidateBounds,
        Matrix4x4 candidateTransform, HousingTemplate neighbor, CryBounds neighborBounds, Matrix4x4 neighborTransform)
    {
        var candidateHasGarden = candidate.GardenRadius > 0;
        var neighborHasGarden = neighbor.GardenRadius > 0;
        var candidateGarden = HousingGeometryAssets.GardenBounds(candidate, candidateBounds, candidateTransform);
        // Native393349e0 compares the candidate's alley-adjusted cells with the neighbor's full plot.
        var neighborGarden = HousingGeometryAssets.GardenBounds(neighbor, neighborBounds, neighborTransform, false);
        if (candidateHasGarden && neighborHasGarden)
        {
            // 39331f30 uses strict XY overlap and deliberately ignores the height of two plots.
            return candidateGarden.Min.X < neighborGarden.Max.X && candidateGarden.Min.Y < neighborGarden.Max.Y &&
                neighborGarden.Min.X < candidateGarden.Max.X && neighborGarden.Min.Y < candidateGarden.Max.Y;
        }
        var candidateBox = candidateHasGarden
            ? new CryBox(candidateGarden.Center, candidateGarden.HalfSize, Matrix4x4.Identity)
            : HousingGeometryAssets.HouseBounds(candidateBounds, candidateTransform);
        var neighborBox = neighborHasGarden
            ? new CryBox(neighborGarden.Center, neighborGarden.HalfSize, Matrix4x4.Identity)
            : HousingGeometryAssets.HouseBounds(neighborBounds, neighborTransform);
        // 39332c10 and 390318f0 use the complete separating-axis box test, including touching faces.
        return CryGeometryQueries.IntersectBox(new CryGeometryPart(neighborBox, Matrix4x4.Identity,
                CryGeometryLayerRules.Solid, "", "house"), Matrix4x4.Identity, candidateBox, Matrix4x4.Identity)
            != CryIntersection.Clear;
    }

    public static ErrorMessageType CheckWater(uint categoryId, float positionZ, float waterSurfaceZ)
    {
        if (!float.IsFinite(positionZ) || !float.IsFinite(waterSurfaceZ))
            return ErrorMessageType.HouseCannotLocateInvalidArea;
        // 39334380 tests the pivot, with equality on the land side. 398970b0 selects categories 7 and 15.
        var underwater = waterSurfaceZ > positionZ;
        return categoryId is 7 or 15
            ? underwater ? ErrorMessageType.NoErrorMessage : ErrorMessageType.HouseUnderWaterOnly
            : underwater ? ErrorMessageType.HouseLandOnly : ErrorMessageType.NoErrorMessage;
    }

    public static bool IsWithinRange(Vector3 player, Vector3 house, float gardenRadius) =>
        IsFinite(player) && IsFinite(house) && float.IsFinite(gardenRadius) && gardenRadius >= 0 &&
        MathF.Floor(Vector3.Distance(player, house)) <= gardenRadius + 30f;

    public static Vector2 SnapPlot(Vector2 position, float gardenRadius)
    {
        // Native39897170 computes the number of four-meter cells in the plot.
        var width = (int)((gardenRadius * 2f + 3f) / 4f);
        if (width >= 12)
            throw new ArgumentOutOfRangeException(nameof(gardenRadius));
        var halfFootprint = width * 2f;
        return new Vector2(
            MathF.Truncate((position.X - halfFootprint + 2f) / 4f) * 4f + halfFootprint,
            MathF.Truncate((position.Y - halfFootprint + 2f) / 4f) * 4f + halfFootprint);
    }

    public static HousingConstructionPose Resolve(HousingTemplate template, Vector3 requestedPosition, float yaw,
        Vector3 playerPosition, Vector3 modelMin, Vector3 modelMax, Func<float, float, float> sampleHeight,
        Func<int, int, float> sampleRawHeight,
        float terrainGridSize, Vector2? strongholdGridPhase = null)
    {
        var invalid = ErrorMessageType.HouseCannotLocateInvalidArea;
        if (template == null || sampleHeight == null || sampleRawHeight == null || !IsFinite(requestedPosition) || !IsFinite(playerPosition) ||
            !IsFinite(modelMin) || !IsFinite(modelMax) || modelMin.X > modelMax.X || modelMin.Y > modelMax.Y ||
            modelMin.Z > modelMax.Z || !float.IsFinite(yaw) || !float.IsFinite(template.GardenRadius) ||
            template.GardenRadius < 0 || requestedPosition.X < 0 || requestedPosition.Y < 0)
            return new(requestedPosition, yaw, invalid);

        var position = requestedPosition;
        var error = ErrorMessageType.NoErrorMessage;
        if (template.CategoryId == 5)
        {
            if (strongholdGridPhase is not { } phase || !float.IsFinite(phase.X) || !float.IsFinite(phase.Y))
                return new(position, yaw, invalid);
            position.X = MathF.Floor((position.X + phase.X) / 20f + 0.5f) * 20f - phase.X;
            position.Y = MathF.Floor((position.Y + phase.Y) / 20f + 0.5f) * 20f - phase.Y;
            var terrain = sampleHeight(position.X, position.Y);
            if (!float.IsFinite(terrain))
                return new(position, yaw, invalid);
            position.Z = MathF.Ceiling(terrain / 3f) * 3f;
            yaw = MathF.Floor(yaw / (MathF.PI / 2f) + 0.5f) * (MathF.PI / 2f);
        }
        else
        {
            if (template.GardenRadius > 0)
            {
                if ((int)((template.GardenRadius * 2f + 3f) / 4f) >= 12)
                    return new(position, yaw, invalid);
                var snapped = SnapPlot(new(position.X, position.Y), template.GardenRadius);
                position.X = snapped.X;
                position.Y = snapped.Y;
                position.Z = sampleHeight(position.X, position.Y);
            }
            if (!float.IsFinite(position.Z))
                return new(position, yaw, invalid);
            var rotation = Matrix4x4.CreateRotationZ(yaw);
            var center = (modelMin + modelMax) * 0.5f;
            var heights = new List<float>();
            void Sample(Vector3 point)
            {
                var worldPoint = Vector3.Transform(point, rotation) + position;
                heights.Add(sampleHeight(worldPoint.X, worldPoint.Y));
            }
            Sample(new(modelMin.X, modelMin.Y, modelMin.Z));
            Sample(new(modelMin.X, modelMax.Y, modelMin.Z));
            Sample(new(modelMax.X, modelMin.Y, modelMin.Z));
            Sample(new(modelMax.X, modelMax.Y, modelMin.Z));
            if (template.AutoZ)
            {
                if (!float.IsFinite(terrainGridSize) || terrainGridSize <= 0)
                    return new(position, yaw, invalid);
                Sample(new(center.X, modelMin.Y, center.Z));
                Sample(new(center.X, modelMax.Y, center.Z));
                Sample(new(modelMin.X, center.Y, center.Z));
                Sample(new(modelMax.X, center.Y, center.Z));
                var half = (modelMax - modelMin) * 0.5f;
                var radius = MathF.Sqrt(half.X * half.X + half.Y * half.Y);
                var firstX = MathF.Max(0, MathF.Floor((position.X - radius - terrainGridSize) / terrainGridSize) * terrainGridSize);
                var firstY = MathF.Max(0, MathF.Floor((position.Y - radius - terrainGridSize) / terrainGridSize) * terrainGridSize);
                var lastX = MathF.Floor((position.X + radius + terrainGridSize) / terrainGridSize) * terrainGridSize;
                var lastY = MathF.Floor((position.Y + radius + terrainGridSize) / terrainGridSize) * terrainGridSize;
                var inverseRotation = Matrix4x4.CreateRotationZ(-yaw);
                for (var x = firstX; x <= lastX; x += terrainGridSize)
                for (var y = firstY; y <= lastY; y += terrainGridSize)
                {
                    var local = Vector3.Transform(new Vector3(x - position.X, y - position.Y, center.Z), inverseRotation);
                    if (local.X >= modelMin.X && local.X <= modelMax.X && local.Y >= modelMin.Y && local.Y <= modelMax.Y)
                        // Native39337fb0 uses +0x204 for grid vertices, not the +0x1fc perimeter query.
                        heights.Add(sampleRawHeight(checked((int)x), checked((int)y)));
                }
            }
            else
                Sample(new(center.X, center.Y, modelMin.Z));
            if (heights.Any(height => !float.IsFinite(height)))
                return new(position, yaw, invalid);
            var minimum = heights.Min();
            var maximum = heights.Max();
            if (template.AutoZ)
            {
                position.Z = maximum;
                if (minimum < modelMin.Z + maximum)
                    error = ErrorMessageType.HouseCannotLocateTerrainTooLow;
            }
            else if (maximum - minimum > 6f)
                error = ErrorMessageType.HouseCannotLocateTerrainTooLow;
            else
                position.Z = minimum;
        }
        if (!IsWithinRange(playerPosition, position, template.GardenRadius))
            error = ErrorMessageType.TooFarAway;
        return new(position, yaw, error);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
