namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed partial class CryGeometryResolver
{
    private CryGeometryAsset LoadCharacterLodBounds(string path, CryGeometryAsset asset)
    {
        var bones = asset.CharacterBoundsBones.ToHashSet();
        // Native315ec4c0 builds these names. Native31527310 unions all six skin LOD palettes.
        for (var lod = 1; lod < 6; lod++)
        {
            var lodPath = AssetPath(path[..^4] + "_lod" + lod + ".chr");
            using var stream = OpenOptionalModelFile(lodPath);
            if (stream == null)
                break;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var skin = ReadCgf(buffer.ToArray(), lodPath);
            bones.UnionWith(skin.CharacterBoundsBones);
        }
        return asset with { CharacterBoundsBones = bones.Order().ToArray() };
    }
}
