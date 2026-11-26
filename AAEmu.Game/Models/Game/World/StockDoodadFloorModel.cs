using System.Collections.Frozen;
using System.Numerics;
using System.Text.Json;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.Game.Models.Game.World;

internal sealed record StockDoodadFloorModel(string Sha256, CryBounds Bounds)
{
    private static readonly FrozenDictionary<string, StockDoodadFloorModel> s_files = ReadCatalog();

    internal static bool IncludesFile(string normalizedFile) => s_files.ContainsKey(normalizedFile);

    internal static StockDoodadFloorModel Find(string model)
    {
        if (model?.StartsWith("cgf://", StringComparison.OrdinalIgnoreCase) != true)
            return null;
        return s_files.GetValueOrDefault(CryGeometryResolver.AssetPath(model[6..]));
    }

    private static FrozenDictionary<string, StockDoodadFloorModel> ReadCatalog()
    {
        using var stream = typeof(StockDoodadFloorModel).Assembly.GetManifestResourceStream("AAEmu.Game.StockDoodadFloorBounds.json");
        using var document = JsonDocument.Parse(stream!);
        var files = new Dictionary<string, StockDoodadFloorModel>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.GetProperty("bounds").EnumerateObject())
        {
            var values = row.Value;
            Vector3 Metres(int i) => new(values[i].GetInt32() / 1000f, values[i + 1].GetInt32() / 1000f, values[i + 2].GetInt32() / 1000f);
            files.Add("game/" + row.Name, new StockDoodadFloorModel(values[0].GetString(), new CryBounds(Metres(1), Metres(4))));
        }
        return files.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
