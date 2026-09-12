namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed partial class CryGeometryResolver
{
    /// <summary>Applies a doodad phase clip to direct or prefab character models.</summary>
    public CryGeometryAsset LoadAnimationPose(string modelUri, string animationName, double elapsedSeconds, bool loop)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        ArgumentNullException.ThrowIfNull(animationName);
        var uri = Normalize(modelUri);
        if (uri.StartsWith("prefab://", StringComparison.Ordinal))
            return LoadPrefab(uri[9..], elapsedSeconds, animationName, loop);
        var asset = LoadPose(uri, elapsedSeconds);
        return (uri.StartsWith("cga://", StringComparison.Ordinal) || uri.StartsWith("cga_loop://", StringComparison.Ordinal)) &&
            asset.HasModelBounds ? ApplyRequestedAnimation(uri, asset, animationName, elapsedSeconds, loop) : asset;
    }

    private CryGeometryAsset ApplyRequestedAnimation(string uri, CryGeometryAsset current, string name,
        double elapsedSeconds, bool loop)
    {
        if (current.CgaAnimation != null)
        {
            if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                var path = Normalize(uri);
                var separator = path.IndexOf("://", StringComparison.Ordinal);
                if (separator >= 0)
                    path = path[(separator + 3)..];
                var clipPath = AssetPath(path[..^4] + "_" + name.ToLowerInvariant() + ".anm");
                if (!_cgaClips.ContainsKey(clipPath))
                {
                    using var stream = OpenOptionalModelFile(clipPath);
                    if (stream == null)
                        return current;
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    _cgaClips.TryAdd(clipPath, current.CgaAnimation.WithClip(buffer.ToArray()));
                }
            }
            return LoadCgaPose(uri, name, elapsedSeconds, loop);
        }
        if (current.CharacterBones.Count > 0)
        {
            var path = Normalize(uri);
            var separator = path.IndexOf("://", StringComparison.Ordinal);
            if (separator >= 0)
                path = path[(separator + 3)..];
            if (FindCharacterAnimation(ResolveCharacterModelPath(path), name) != null)
                return LoadCharacterPose(uri, name, elapsedSeconds, loop);
        }
        return current;
    }
}
