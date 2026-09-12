using System.Collections.Concurrent;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed partial class CryGeometryResolver
{
    private readonly ConcurrentDictionary<string, CryCgaAnimation> _cgaClips = new(StringComparer.OrdinalIgnoreCase);

    public CryGeometryAsset LoadCgaPose(string modelUri, string animationName, double elapsedSeconds, bool loop)
    {
        var asset = Load(modelUri);
        var animation = asset.CgaAnimation ?? throw new InvalidDataException("The model has no CGA node hierarchy.");
        if (!animationName.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            var path = Normalize(modelUri);
            var separator = path.IndexOf("://", StringComparison.Ordinal);
            if (separator >= 0)
                path = path[(separator + 3)..];
            var clipPath = AssetPath(path[..^4] + "_" + animationName.ToLowerInvariant() + ".anm");
            animation = _cgaClips.GetOrAdd(clipPath, file =>
            {
                using var stream = OpenFile(file);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return animation.WithClip(buffer.ToArray());
            });
        }
        return animation.Sample(asset, elapsedSeconds, loop);
    }
}
