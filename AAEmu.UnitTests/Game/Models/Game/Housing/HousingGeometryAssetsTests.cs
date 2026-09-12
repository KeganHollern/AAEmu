using System.Numerics;
using System.Text;

using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingGeometryAssetsTests
{
    [Test]
    public async Task LoadModel_OnlyEffectsAndMissingAnimation_HasNoPlacementBounds()
    {
        var assets = new HousingGeometryAssets(path => path.EndsWith(".xml", StringComparison.Ordinal)
            ? new MemoryStream(Encoding.UTF8.GetBytes("<PrefabsLibrary><Prefab Name='effect'><Objects><Object Type='Decal'/></Objects></Prefab></PrefabsLibrary>"))
            : null, (_, _) => ["prefab://prefabs/fx.xml/effect", "cga://objects/missing.cga"]);
        var model = assets.LoadModel(1);
        await Assert.That(model.HasModelBounds).IsFalse();
        await Assert.That(model.Parts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CollisionBounds_ProxyOutsideModel_KeepsBothWorldVolumes()
    {
        var model = new CryGeometryAsset(new CryBounds(-Vector3.One, Vector3.One),
            [new CryGeometryPart(new CrySphere(Vector3.Zero, 2), Matrix4x4.CreateTranslation(5, 0, 0), 0x1000, "", "bone")]);
        var bounds = HousingGeometryAssets.CollisionBounds(model, Matrix4x4.CreateTranslation(100, 200, 300));
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(99, 198, 298));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(107, 202, 302));
    }

    [Test]
    public async Task ExactClient_PreviewAndPlacedModels_UseBindAndPersistedClocks()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 runtime pose check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            const string uri = "cga_loop://objects/env/02_harihara/001_housing/housing_castle/hari_gate_castledoor.cga";
            Stream Open(string name) => source.FileExists(name) ? source.GetFileStream(name) : null;
            var resolver = new CryGeometryResolver(Open);
            var assets = new HousingGeometryAssets(Open, (_, _) => [uri]);
            var utcNow = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
            var template = new HousingTemplate { MainModelId = 1 };
            var house = new House { Template = template, PlaceDate = utcNow.AddSeconds(-0.5) };
            var doodad = new Doodad { Template = new DoodadTemplate { Model = uri }, PhaseTime = house.PlaceDate };
            var expected = resolver.LoadPose(uri, 0.5);
            await Assert.That(expected.Parts.Count).IsGreaterThan(0);
            await Assert.That(assets.LoadHouse(template).Bounds).IsEqualTo(resolver.Load(uri).Bounds);
            await Assert.That(assets.LoadDoodad(doodad.Template).Bounds).IsEqualTo(resolver.Load(uri).Bounds);
            await Assert.That(assets.LoadHouse(house, utcNow).Bounds).IsEqualTo(expected.Bounds);
            await Assert.That(assets.LoadDoodad(doodad, utcNow).Parts.Select(part => part.Transform)
                .SequenceEqual(expected.Parts.Select(part => part.Transform))).IsTrue();
            house.PlaceDate = utcNow.AddDays(1);
            await Assert.That(assets.LoadHouse(house, utcNow).Bounds).IsEqualTo(resolver.LoadPose(uri, 0).Bounds);
        }
        finally { source.Close(); }
    }

    [Test]
    public async Task GardenBounds_ReservesAlleyAndUsesRotatedModelHeight()
    {
        var template = new HousingTemplate { GardenRadius = 8, Alley = 1, ExtraHeightAbove = 2, ExtraHeightBelow = 3 };
        var local = new CryBounds(new(-6, -4, -1), new(6, 4, 7));
        var transform = Matrix4x4.CreateRotationZ(MathF.PI / 4) * Matrix4x4.CreateTranslation(100, 100, 10);
        var bounds = HousingGeometryAssets.GardenBounds(template, local, transform);
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(93, 93, 6));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(107, 107, 19));
    }

    [Test]
    public async Task GardenBounds_ZeroRadiusUsesAuthoredBounds()
    {
        var template = new HousingTemplate { GardenRadius = 0 };
        var local = new CryBounds(new(-6, -4, -1), new(6, 4, 7));
        var bounds = HousingGeometryAssets.GardenBounds(template, local, Matrix4x4.CreateTranslation(100, 100, 10));
        await Assert.That(bounds.Min).IsEqualTo(new Vector3(94, 96, 9));
        await Assert.That(bounds.Max).IsEqualTo(new Vector3(106, 104, 17));
        await Assert.That(HousingDecorationGeometry.IsWithinSelectionRange(bounds, new Vector3(100, 100, 100))).IsFalse();
    }

    [Test]
    public async Task GardenBounds_FullPlotDoesNotReserveAlley()
    {
        var template = new HousingTemplate { GardenRadius = 8, Alley = 1 };
        var bounds = HousingGeometryAssets.GardenBounds(template, new CryBounds(Vector3.Zero, Vector3.One),
            Matrix4x4.CreateTranslation(100, 100, 0), false);
        await Assert.That(bounds.Min.X).IsEqualTo(92f);
        await Assert.That(bounds.Max.X).IsEqualTo(108f);
    }
}
