using System.Numerics;

namespace AAEmu.Game.Models.Game.Housing;

public readonly record struct HousingConstructionPose(Vector3 Position, float Yaw, ErrorMessageType Error)
{
    public bool IsValid => Error == ErrorMessageType.NoErrorMessage;
}

/// <summary>Pure r208022 placement pose rules. World collision and area rules run separately.</summary>
public static class HousingConstructionGeometry
{
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
        float terrainGridSize, Vector2? strongholdGridPhase = null)
    {
        var invalid = ErrorMessageType.HouseCannotLocateInvalidArea;
        if (template == null || sampleHeight == null || !IsFinite(requestedPosition) || !IsFinite(playerPosition) ||
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
                        heights.Add(sampleHeight(x, y));
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
