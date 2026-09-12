using System.Numerics;
using System.Text;

using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryPrefabWaterTests
{
    [Test]
    public async Task LoadWater_KeepsXmlOrderWithoutOpeningMeshesOrAnimation()
    {
        var resolver = Resolver($"""
            <PrefabsLibrary><Prefab Name="water"><Objects>
              <Object Type="Entity"><Properties object_Model="cga://objects/animated.chr"><Animation bPlaying="1" /></Properties></Object>
              {WaterXml("first", 10)}
              {WaterXml("second", 12)}
            </Objects></Prefab></PrefabsLibrary>
            """);
        var water = resolver.LoadWater("prefab://Prefabs//water.xml/water");
        await Assert.That(water.Select(volume => volume.Name).SequenceEqual(["first", "second"])).IsTrue();
        await Assert.That(ReferenceEquals(water, resolver.LoadWater("PREFAB://prefabs/water.xml/water"))).IsTrue();
        await Assert.That(resolver.LoadWater("cga://objects/animated.chr").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Query_ChildAndInstanceTransformsPrecedeVerticalProjection()
    {
        var resolver = Resolver($"""
            <PrefabsLibrary><Prefab Name="water"><Objects>
              {WaterXml("first", 30, "Pos=\"10,20,30\" Scale=\"2,2,2\" Rotate=\"0.70710677,0,0,0.70710677\"", nonplanar: true)}
            </Objects></Prefab></PrefabsLibrary>
            """);
        var instance = new CryWaterVolumeInstance(resolver.LoadWater("prefab://prefabs/water.xml/water"),
            Matrix4x4.CreateTranslation(100, 200, 300));
        var geometry = new HousingWaterGeometry();
        await Assert.That(geometry.GetWaterLevel(new(106, 224, 329), 0, [instance])).IsEqualTo(330f).Within(0.0001f);
        await Assert.That(geometry.GetWaterLevel(new(114, 224, 329), 0, [instance])).IsEqualTo(0f);
        // The native depth is 5 metres. The child scale does not multiply it.
        await Assert.That(geometry.GetWaterLevel(new(106, 224, 324.99f), 0, [instance])).IsEqualTo(0f);
        var contour = instance.Volumes[0].GetPhysicsContour(instance.Transform, 0, out var normal);
        await Assert.That(Vector3.Distance(contour[1], new(110, 228, 330)) < 0.0001f).IsTrue();
        await Assert.That(normal).IsEqualTo(Vector3.UnitZ);
    }

    [Test]
    public async Task Query_RotatedPlaneUsesNativeNormalProjectionAndDepth()
    {
        var rotation = Matrix4x4.CreateRotationY(MathF.Asin(0.6f));
        var water = new CryPrefabWaterVolume("slope", Square(), rotation * Matrix4x4.CreateTranslation(10, 20, 30), 5);
        var instance = new CryWaterVolumeInstance([water], Matrix4x4.Identity);
        var geometry = new HousingWaterGeometry();
        // The plane at local (2,2,0) is (11.6,22,28.8). The normal is (0.6,0,0.8).
        await Assert.That(geometry.GetWaterLevel(new(11.6f, 22, 27.8f), 0, [instance])).IsEqualTo(28.44f).Within(0.0001f);
        await Assert.That(geometry.GetWaterLevel(new(11.6f, 22, 22.5f), 0, [instance])).IsEqualTo(0f);
    }

    [Test]
    public async Task Query_HeightOffsetRaisesSurfaceAndKeepsTheBottom()
    {
        var water = new CryPrefabWaterVolume("water", Square(), Matrix4x4.CreateTranslation(0, 0, 10), 5);
        var geometry = new HousingWaterGeometry();
        var instance = new CryWaterVolumeInstance([water], Matrix4x4.Identity, 3);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 5.01f), 0, [instance])).IsEqualTo(13f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 5), 0, [instance])).IsEqualTo(0f);
    }

    [Test]
    public async Task Query_FourNewestChildrenExcludeOlderHigherWater()
    {
        var volumes = new[] { 40f, 10f, 11f, 12f, 13f }.Select((surface, index) =>
            new CryPrefabWaterVolume(index.ToString(), Square(), Matrix4x4.CreateTranslation(0, 0, surface), 100)).ToArray();
        var instance = new CryWaterVolumeInstance(volumes, Matrix4x4.Identity);
        await Assert.That(new HousingWaterGeometry().GetWaterLevel(new(2, 2, 9), 0, [instance])).IsEqualTo(13f);
    }

    [Test]
    public async Task Query_FourNewestInstancesShareTheLimitWithStaticWater()
    {
        var geometry = new HousingWaterGeometry();
        var staticWater = HousingWaterGeometryTests.Water(id: 10, surface: 40, depth: 100);
        geometry.Add(staticWater, Vector3.Zero);
        var instances = Enumerable.Range(10, 4).Select(surface => new CryWaterVolumeInstance(
            [new CryPrefabWaterVolume("water", Square(), Matrix4x4.CreateTranslation(0, 0, surface), 100)],
            Matrix4x4.Identity)).ToArray();
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0, instances)).IsEqualTo(13f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0, instances[..3])).IsEqualTo(40f);
        await Assert.That(geometry.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Query_CurrentPhaseAndRemovedInstanceDoNotLeaveWaterInTheStaticCache()
    {
        var resolver = Resolver($"""
            <PrefabsLibrary>
              <Prefab Name="wet"><Objects>{WaterXml("water", 10)}</Objects></Prefab>
              <Prefab Name="dry"><Objects><Object Type="Comment" Name="origin" /></Objects></Prefab>
            </PrefabsLibrary>
            """);
        var geometry = new HousingWaterGeometry();
        var wet = new CryWaterVolumeInstance(resolver.LoadWater("prefab://prefabs/water.xml/wet"), Matrix4x4.Identity);
        var dry = new CryWaterVolumeInstance(resolver.LoadWater("prefab://prefabs/water.xml/dry"), Matrix4x4.Identity);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0, [wet])).IsEqualTo(10f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0, [dry])).IsEqualTo(0f);
        await Assert.That(geometry.GetWaterLevel(new(2, 2, 9), 0, [])).IsEqualTo(0f);
    }

    [Test]
    public async Task WaterOnlyPrefab_HasNativeBoundsWithoutSolidParts()
    {
        var resolver = Resolver($"""
            <PrefabsLibrary><Prefab Name="water"><Objects>
              {WaterXml("water", 30, "Pos=\"10,20,30\" Scale=\"2,2,2\"")}
            </Objects></Prefab></PrefabsLibrary>
            """);
        var asset = resolver.Load("prefab://prefabs/water.xml/water");
        await Assert.That(asset.HasModelBounds).IsTrue();
        await Assert.That(asset.Parts.Count).IsEqualTo(0);
        await Assert.That(asset.WaterVolumes.Count).IsEqualTo(1);
        // Native GetLocalBBox subtracts the render AABB center, then the prefab getter applies its child transform.
        await Assert.That(asset.Bounds.Min).IsEqualTo(new Vector3(2, 12, 25));
        await Assert.That(asset.Bounds.Max).IsEqualTo(new Vector3(18, 28, 35));
    }

    [Test]
    public async Task WaterWithThreePoints_DoesNotCreateANativeRenderNode()
    {
        var resolver = Resolver("""
            <PrefabsLibrary><Prefab Name="water"><Objects><Object Type="WaterVolume"><Points>
              <Point Pos="0,0,0" /><Point Pos="4,0,0" /><Point Pos="0,4,0" />
            </Points></Object></Objects></Prefab></PrefabsLibrary>
            """);
        await Assert.That(resolver.LoadWater("prefab://prefabs/water.xml/water").Count).IsEqualTo(0);
        await Assert.That(resolver.Load("prefab://prefabs/water.xml/water").HasModelBounds).IsFalse();
    }

    [Test]
    public async Task LoadWater_ExactFalconyPrefabUsesItsAuthoredDepthAndPoints()
    {
        var root = Environment.GetEnvironmentVariable("AAEMU_PREFAB_WATER_CLIENT_ROOT");
        Skip.Unless(!string.IsNullOrEmpty(root), "Set AAEMU_PREFAB_WATER_CLIENT_ROOT to extracted prefab files.");
        var resolver = new CryGeometryResolver(path => File.OpenRead(Path.Combine(root!, path)));
        var water = resolver.LoadWater("prefab://prefabs/e_falcony_plateau.xml/e_falcony_plateau.water_b");
        await Assert.That(water.Count).IsEqualTo(1);
        await Assert.That(water[0].Points.Count).IsEqualTo(8);
        await Assert.That(water[0].Depth).IsEqualTo(30f);
        var parent = Matrix4x4.CreateTranslation(22922.07f, 9493.238f, 527.0209f);
        var point = Vector3.Transform(new Vector3(20, 0, 0), water[0].Transform * parent) - Vector3.UnitZ;
        await Assert.That(new HousingWaterGeometry().GetWaterLevel(point, 0, [new CryWaterVolumeInstance(water, parent)]))
            .IsEqualTo(547.9945f).Within(0.001f);
        // Native tests the Name attribute. Comment220 has Comment="origin", which does not rebase the prefab.
        await Assert.That(water[0].Transform.Translation.Z).IsEqualTo(20.973572f);
    }

    [Test]
    public async Task LoadWater_OriginAfterTheWaterRebasesEveryChildBeforeTheInstanceTransform()
    {
        var resolver = Resolver($"""
            <PrefabsLibrary><Prefab Name="water"><Objects>
              <Object Type="Comment" Name="origin" Pos="100,100,100" />
              {WaterXml("water", 30, "Pos=\"10,20,30\" Scale=\"2,2,2\" Rotate=\"0.70710677,0,0,0.70710677\"")}
              <Object Type="Comment" Name="origin" Pos="1,2,3" />
            </Objects></Prefab></PrefabsLibrary>
            """);
        var volumes = resolver.LoadWater("prefab://prefabs/water.xml/water");
        await Assert.That(volumes[0].Transform.Translation).IsEqualTo(new Vector3(9, 18, 27));
        var parent = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(100, 200, 300);
        var contour = volumes[0].GetPhysicsContour(parent, 0, out _);
        await Assert.That(Vector3.Distance(contour[0], new(82, 209, 327)) < 0.0001f).IsTrue();
        await Assert.That(resolver.Load("prefab://prefabs/water.xml/water").Helpers.Count).IsEqualTo(0);
    }

    private static CryGeometryResolver Resolver(string xml) => new(path => path.EndsWith(".xml", StringComparison.Ordinal)
        ? new MemoryStream(Encoding.UTF8.GetBytes(xml)) : throw new InvalidOperationException($"Unexpected model read: {path}"));

    private static Vector3[] Square() => [new(0, 0, 0), new(4, 0, 0), new(4, 4, 0), new(0, 4, 0)];

    private static string WaterXml(string name, float surface, string transform = null, bool nonplanar = false) => $"""
        <Object Type="WaterVolume" Name="{name}" VolumeDepth="5" {transform ?? $"Pos=\"0,0,{surface}\""}>
          <Points><Point Pos="0,0,0" /><Point Pos="4,0,{(nonplanar ? 5 : 0)}" />
          <Point Pos="4,4,0" /><Point Pos="0,4,0" /></Points>
        </Object>
        """;
}
