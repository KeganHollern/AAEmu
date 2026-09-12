using System.Collections.Concurrent;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed partial class CryGeometryResolver
{
    private readonly ConcurrentDictionary<string, CryCharacterDefinition> _characterDefinitions = new(StringComparer.OrdinalIgnoreCase);

    private CryCharacterDefinition ReadCharacterDefinition(string path) => _characterDefinitions.GetOrAdd(AssetPath(path), file =>
    {
        using var stream = OpenFile(file);
        return CryCharacterDefinition.Read(stream);
    });

    private string ResolveCharacterModelPath(string path) => path.EndsWith(".cdf", StringComparison.Ordinal)
        ? ReadCharacterDefinition(path).ModelPath : path;

    private CryGeometryAsset LoadCharacterDefinition(string path)
    {
        var definition = ReadCharacterDefinition(path);
        if (!definition.ModelPath.EndsWith(".chr", StringComparison.Ordinal))
            throw new NotSupportedException("Character definition needs an authored CHR model.");
        var asset = Load(definition.ModelPath);
        return string.IsNullOrEmpty(definition.MaterialPath) ? asset : asset with
        {
            Parts = asset.Parts.Select(part => part with { MaterialPath = AssetPath(definition.MaterialPath) }).ToArray()
        };
    }
}
