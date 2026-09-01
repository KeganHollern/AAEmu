using System.Globalization;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Utils.Scripts;

public readonly record struct CommandSurfaceResult(
    Vector3 QueryPosition,
    float? TerrainHeight,
    GroundSurfaceResult SelectedGround,
    float? LegacyHeight)
{
    public static CommandSurfaceResult Resolve(WorldTemplate template, Vector3 queryPosition)
    {
        float? terrainHeight = template.TryGetHeight(queryPosition.X, queryPosition.Y, out var sampledTerrainHeight)
            ? sampledTerrainHeight
            : null;

        var selectedGround = new GroundSurfaceResult(0f, GroundSurfaceSource.None, GroundSurfaceDecision.None,
            GroundSurfaceFailure.Unavailable, null);
        if (template.GeoData is not null)
            template.GeoData.TryGetGroundSurface(queryPosition, out selectedGround);

        float? legacyHeight = template.GeoData is not null &&
                              template.GeoData.TryGetHeight(queryPosition, out var sampledLegacyHeight)
            ? sampledLegacyHeight
            : null;

        return new CommandSurfaceResult(queryPosition, terrainHeight, selectedGround, legacyHeight);
    }

    public bool TryGetSelectedGroundHeight(out float height)
    {
        if (SelectedGround.IsResolved)
        {
            height = SelectedGround.Height;
            return true;
        }

        height = 0f;
        return false;
    }

    public string FormatHeights()
    {
        var selectedGround = SelectedGround.IsResolved
            ? $"selectedGround={Format(SelectedGround.Height)} source={SelectedGround.Source} " +
              $"decision={SelectedGround.Decision} failure={SelectedGround.Failure}"
            : $"selectedGround=n/a source={SelectedGround.Source} " +
              $"decision={SelectedGround.Decision} failure={SelectedGround.Failure}";
        return $"terrain={Format(TerrainHeight)} {selectedGround} legacyHeight={Format(LegacyHeight)}";
    }

    public static string Format(float value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string Format(float? value) => value.HasValue ? Format(value.Value) : "n/a";
}
