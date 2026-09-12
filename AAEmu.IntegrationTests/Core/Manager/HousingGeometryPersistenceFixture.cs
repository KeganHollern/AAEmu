using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    private static void ConfigureHousingGeometry(HousingManager housing, bool floor = true, bool obstacle = false,
        bool construction = false)
    {
        var geometry = new HousingGeometryAssets(path => path.EndsWith("heightmap.dat", StringComparison.Ordinal)
            ? HousingFlatTerrain(300) : null, (_, _) => []);
        housing.GeometryAssets = geometry;
        var houseBounds = construction
            ? new CryBounds(new Vector3(-1, -1, 0), new Vector3(1, 1, 2))
            : new CryBounds(new Vector3(-10, -10, 0), new Vector3(10, 10, 10));
        var parts = new List<CryGeometryPart>();
        if (floor && !construction)
            parts.Add(HousingBox(new Vector3(0, 0, 2.5f), new Vector3(10, 10, 0.5f)));
        if (obstacle)
            parts.Add(HousingBox(new Vector3(1, 2, 3.75f), new Vector3(0.25f)));
        HousingField<ConcurrentDictionary<uint, CryGeometryAsset>>(geometry, "_models")[1] = new(houseBounds, parts);
        var resolver = HousingField<CryGeometryResolver>(geometry, "_resolver");
        HousingField<ConcurrentDictionary<string, CryGeometryAsset>>(resolver, "_cache")["cgf://housing-test-decoration.cgf"] =
            new(new CryBounds(new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 1)),
                [HousingBox(new Vector3(0, 0, 0.5f), new Vector3(0.5f))]);
    }

    private static CryGeometryPart HousingBox(Vector3 center, Vector3 halfSize) =>
        new(new CryBox(center, halfSize, Matrix4x4.Identity), Matrix4x4.Identity, CryGeometryLayerRules.Solid, "", "");

    private static MemoryStream HousingFlatTerrain(float height)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(24); writer.Write(0); writer.Write(4096); writer.Write(2);
        writer.Write(64); writer.Write(128); writer.Write(0.0625f); writer.Write(-100f);
        writer.Write(new byte[128]);
        writer.Write(5);
        foreach (var value in new[] { 0f, 0f, height, 1024f, 1024f, height }) writer.Write(value);
        writer.Write(false); writer.Write(height); writer.Write(1f / 32); writer.Write(2); writer.Write(0);
        writer.Write(new byte[8 + 20 + 36]);
        stream.Position = 4;
        writer.Write((int)stream.Length);
        stream.Position = 0;
        return stream;
    }

    private static T HousingField<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
