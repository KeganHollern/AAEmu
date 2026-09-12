using System.Collections.Concurrent;

using AAEmu.Game.Models.CryEngine.Objects;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>Resolves static world models with the native missing-brush fallback.</summary>
public sealed class CryWorldGeometryResolver(Func<string, CryGeometryAsset> loadModel)
{
    private readonly ConcurrentDictionary<(ObjectDataType Kind, string Uri), CryGeometryAsset> _models = [];

    public CryGeometryAsset Load(CryWorldObjectInstance instance) => instance.Asset ??
        _models.GetOrAdd((instance.Kind, CryGeometryResolver.Normalize(instance.ModelUri)), LoadModel);

    private CryGeometryAsset LoadModel((ObjectDataType Kind, string Uri) key)
    {
        try
        {
            return loadModel(key.Uri);
        }
        catch (FileNotFoundException error) when (key.Kind == ObjectDataType.Brush &&
            RootCgfPath(key.Uri) is { } root && RootCgfPath(error.FileName) == root)
        {
            // Native3015eca0 substitutes the default object after a failed brush stream.
            // Both r208022 default.cgf and box_nodraw.cgf contain no physics proxies.
            return loadModel("objects/box_nodraw.cgf");
        }
    }

    private static string RootCgfPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = CryGeometryResolver.Normalize(path);
        if (path.StartsWith("cgf://", StringComparison.Ordinal))
            path = path[6..];
        if (path.Contains("://", StringComparison.Ordinal) || !path.EndsWith(".cgf", StringComparison.Ordinal))
            return null;
        return path.StartsWith("game/", StringComparison.Ordinal) ? path : "game/" + path;
    }
}
