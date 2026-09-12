using System.Numerics;
using System.Xml.Linq;

namespace AAEmu.Game.Models.CryEngine.Physics;

public sealed partial class CryGeometryResolver
{
    private const string MissingBrushModel = "objects/box_nodraw.cgf";

    private System.IO.Stream OpenOptionalModelFile(string path)
    {
        try { return openFile(path); }
        catch (FileNotFoundException) { return null; }
    }

    private CryGeometryAsset ResolveMissingModel(string scheme, string path)
    {
        if (path == AssetPath(MissingBrushModel))
            throw new FileNotFoundException("Missing native fallback geometry.", path);
        // Native391114f0 keeps an empty prefab when its animation object fails to load.
        if (scheme is "cga" or "cga_loop")
            return EmptyModel();
        // Native3010ded0 and3015eca0 substitute the default stat object for a missing brush.
        return Load(MissingBrushModel);
    }

    private XDocument ReadPrefabLibrary(string path)
    {
        using var stream = OpenOptionalModelFile(path);
        return stream == null ? new XDocument() : XDocument.Load(stream);
    }

    private static CryGeometryAsset EmptyModel() => new(new CryBounds(Vector3.Zero, Vector3.Zero), [])
    {
        HasModelBounds = false
    };
}
